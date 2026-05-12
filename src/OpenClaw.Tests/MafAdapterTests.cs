#if OPENCLAW_ENABLE_MAF_EXPERIMENT
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MafAdapterTests
{
    [Fact]
    public void MafCapabilities_JitMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "jit",
            EffectiveMode = GatewayRuntimeMode.Jit,
            DynamicCodeSupported = true
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public void MafCapabilities_AotMode_IsSupported()
    {
        var runtimeState = new GatewayRuntimeState
        {
            RequestedMode = "aot",
            EffectiveMode = GatewayRuntimeMode.Aot,
            DynamicCodeSupported = false
        };

        MafCapabilities.EnsureSupported(runtimeState);
    }

    [Fact]
    public async Task MafSessionStateStore_RoundTripsSerializedSession_WhenHistoryMatches()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-roundtrip");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);
            var savedState = NormalizeJson(await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions: null, CancellationToken.None));

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);
            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(savedState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void MafAgentRuntimeFactory_WithDelegationEnabled_AddsDelegateTool()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var factory = new MafAgentRuntimeFactory(
            new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
            new MafSessionStateStore(
                new GatewayConfig(),
                Options.Create(new MafOptions()),
                NullLogger<MafSessionStateStore>.Instance),
            new MafTelemetryAdapter(),
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance);

        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-delegation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var runtime = Assert.IsType<MafAgentRuntime>(factory.Create(new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = new GatewayConfig
                {
                    Memory = new MemoryConfig
                    {
                        StoragePath = storagePath
                    },
                    Llm = new LlmProviderConfig
                    {
                        Provider = "test-maf",
                        Model = "maf-test-model"
                    },
                    Delegation = new DelegationConfig
                    {
                        Enabled = true,
                        Profiles = new Dictionary<string, AgentProfile>(StringComparer.Ordinal)
                        {
                            ["reviewer"] = new()
                            {
                                Name = "reviewer",
                                SystemPrompt = "Review code changes.",
                                MaxIterations = 2,
                                MaxHistoryTurns = 4
                            }
                        }
                    }
                },
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                ChatClient = new MafTestChatClient(),
                Tools = [new TestTool()],
                MemoryStore = new FileMemoryStore(storagePath, 4),
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = new TestLlmExecutionService(),
                Skills = [],
                SkillsConfig = new SkillsConfig(),
                WorkspacePath = null,
                PluginSkillDirs = [],
                Logger = NullLogger.Instance,
                Hooks = [],
                RequireToolApproval = false,
                ApprovalRequiredTools = [],
                IsContractTokenBudgetExceeded = null,
                IsContractRuntimeBudgetExceeded = null,
                RecordContractTurnUsage = null,
                AppendContractSnapshot = null
            }));

            var mafTools = runtime.GetDeclaredTools(CreateSession("maf-delegation-tools"));
            Assert.Contains(mafTools, tool => tool is AIFunction function && function.Name == "delegate_agent");
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHashMismatch_RebuildsFreshSession()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-mismatch");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);

            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "history changed"
            });

            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(CancellationToken.None), jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_CorruptedSidecar_RebuildsFreshSession()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-corrupt");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);

            await store.SaveAsync(agent, session, agentSession, CancellationToken.None);
            await File.WriteAllTextAsync(store.GetSessionPath(session.Id), "{not-json", CancellationToken.None);

            var loadedSession = await store.LoadAsync(agent, session, CancellationToken.None);
            var loadedState = NormalizeJson(await agent.SerializeSessionAsync(loadedSession, jsonSerializerOptions: null, CancellationToken.None));
            var freshState = NormalizeJson(await agent.SerializeSessionAsync(await agent.CreateSessionAsync(CancellationToken.None), jsonSerializerOptions: null, CancellationToken.None));

            Assert.Equal(freshState, loadedState);
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafSessionStateStore_SaveFailure_CleansUpTempFile()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var store = CreateStore(storagePath);
            var agent = CreateAgent();
            var session = CreateSession("maf-save-cleanup");
            var agentSession = await CreatePopulatedAgentSessionAsync(agent);
            var path = store.GetSessionPath(session.Id);

            Directory.CreateDirectory(path);

            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveAsync(agent, session, agentSession, CancellationToken.None));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public void MafSessionStateStore_HistoryHash_ChangesWhenModelOverrideChanges()
    {
        var session = CreateSession("maf-hash");
        var baseline = MafSessionStateStore.ComputeHistoryHash(session);
        session.ModelOverride = "gpt-maf";

        var updated = MafSessionStateStore.ComputeHistoryHash(session);

        Assert.NotEqual(baseline, updated);
    }

    [Fact]
    public async Task MafSessionStateStore_HistoryHash_RemainsStableAcrossFileSessionReload()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-sidecar-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        try
        {
            var session = CreateSession("maf-file-hash");
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "[tool_use]",
                ToolCalls =
                [
                    new ToolInvocation
                    {
                        ToolName = "memory",
                        Arguments = """{"action":"write","key":"note","content":"hello"}""",
                        Result = "Saved note: note",
                        Duration = TimeSpan.FromMilliseconds(12)
                    }
                ]
            });
            session.History.Add(new ChatTurn
            {
                Role = "assistant",
                Content = "Tool said: Saved note: note"
            });

            var expectedHash = MafSessionStateStore.ComputeHistoryHash(session);

            var writerStore = new FileMemoryStore(storagePath, 4);
            await writerStore.SaveSessionAsync(session, CancellationToken.None);

            var readerStore = new FileMemoryStore(storagePath, 4);
            var loaded = await readerStore.GetSessionAsync(session.Id, CancellationToken.None);

            Assert.NotNull(loaded);
            Assert.Equal(expectedHash, MafSessionStateStore.ComputeHistoryHash(loaded!));
        }
        finally
        {
            Directory.Delete(storagePath, recursive: true);
        }
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_UsesPresetFilteredToolDeclarations()
    {
        var runtime = CreateRuntime(
            [new NamedTool("allowed_tool"), new NamedTool("blocked_tool")],
            new AllowOnlyToolPresetResolver("allowed_tool"),
            out var llmExecutionService);

        var response = await runtime.RunAsync(CreateSession("maf-filtered-run"), "hello", CancellationToken.None);

        Assert.Equal("ok", response);
        Assert.Equal(["allowed_tool"], llmExecutionService.LastNonStreamingToolNames);
    }

    [Fact]
    public async Task MafAgentRuntime_RunStreamingAsync_UsesPresetFilteredToolDeclarations()
    {
        var runtime = CreateRuntime(
            [new NamedTool("allowed_tool"), new NamedTool("blocked_tool")],
            new AllowOnlyToolPresetResolver("allowed_tool"),
            out var llmExecutionService);

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in runtime.RunStreamingAsync(CreateSession("maf-filtered-stream"), "hello", CancellationToken.None))
            events.Add(evt);

        Assert.NotEmpty(events);
        Assert.Equal(["allowed_tool"], llmExecutionService.LastStreamingToolNames);
    }

    private static MafSessionStateStore CreateStore(string storagePath)
    {
        var config = new GatewayConfig
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath
            }
        };

        return new MafSessionStateStore(
            config,
            Options.Create(new MafOptions()),
            NullLogger<MafSessionStateStore>.Instance);
    }

    private static ChatClientAgent CreateAgent()
    {
        var factory = new MafAgentFactory(
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

        return factory.Create(new MafTestChatClient(), "Test instructions", []);
    }

    private static MafAgentRuntime CreateRuntime(
        IReadOnlyList<ITool> tools,
        IToolPresetResolver toolPresetResolver,
        out CapturingLlmExecutionService llmExecutionService)
    {
        var configStoragePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-runtime-tests", "config", Guid.NewGuid().ToString("N"));
        var memoryStoragePath = Path.Combine(Path.GetTempPath(), "openclaw-maf-runtime-tests", "memory", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configStoragePath);
        Directory.CreateDirectory(memoryStoragePath);

        var services = new ServiceCollection();
        services.AddSingleton(toolPresetResolver);
        services.AddSingleton<IToolPresetResolver>(toolPresetResolver);
        var serviceProvider = services.BuildServiceProvider();

        llmExecutionService = new CapturingLlmExecutionService();
        var factory = new MafAgentRuntimeFactory(
            new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, serviceProvider),
            new MafSessionStateStore(
                new GatewayConfig(),
                Options.Create(new MafOptions()),
                NullLogger<MafSessionStateStore>.Instance),
            new MafTelemetryAdapter(),
            Options.Create(new MafOptions()),
            NullLoggerFactory.Instance);

        return Assert.IsType<MafAgentRuntime>(factory.Create(new AgentRuntimeFactoryContext
        {
            Services = serviceProvider,
            Config = new GatewayConfig
            {
                Memory = new MemoryConfig
                {
                    StoragePath = configStoragePath
                },
                Llm = new LlmProviderConfig
                {
                    Provider = "test-maf",
                    Model = "maf-test-model"
                }
            },
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = "jit",
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            ChatClient = new MafTestChatClient(),
            Tools = tools,
            MemoryStore = new FileMemoryStore(memoryStoragePath, 4),
            RuntimeMetrics = new RuntimeMetrics(),
            ProviderUsage = new ProviderUsageTracker(),
            LlmExecutionService = llmExecutionService,
            Skills = [],
            SkillsConfig = new SkillsConfig(),
            WorkspacePath = null,
            PluginSkillDirs = [],
            Logger = NullLogger.Instance,
            Hooks = [],
            RequireToolApproval = false,
            ApprovalRequiredTools = [],
            IsContractTokenBudgetExceeded = null,
            IsContractRuntimeBudgetExceeded = null,
            RecordContractTurnUsage = null,
            AppendContractSnapshot = null
        }));
    }

    private static Session CreateSession(string sessionId)
    {
        var session = new Session
        {
            Id = sessionId,
            ChannelId = "test",
            SenderId = "user"
        };
        session.History.Add(new ChatTurn
        {
            Role = "user",
            Content = "hello"
        });
        return session;
    }

    private static async Task<AgentSession> CreatePopulatedAgentSessionAsync(ChatClientAgent agent)
    {
        var agentSession = await agent.CreateSessionAsync(CancellationToken.None);
        _ = await agent.RunAsync(
            [new ChatMessage(ChatRole.User, "hello from maf sidecar")],
            agentSession,
            new ChatClientAgentRunOptions(new ChatOptions()),
            CancellationToken.None);
        return agentSession;
    }

    private static string NormalizeJson(JsonElement element)
        => JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = false });

    private sealed class TestTool : ITool
    {
        public string Name => "echo_tool";

        public string Description => "Echo test tool.";

        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            return ValueTask.FromResult("ok");
        }
    }

    private sealed class NamedTool(string name) : ITool
    {
        public string Name => name;

        public string Description => $"Tool {name}.";

        public string ParameterSchema => """{"type":"object"}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            return ValueTask.FromResult(name);
        }
    }

    private sealed class AllowOnlyToolPresetResolver(params string[] allowedTools) : IToolPresetResolver
    {
        private readonly HashSet<string> _allowedTools = allowedTools.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public ResolvedToolPreset Resolve(Session session, IEnumerable<string> availableToolNames)
        {
            _ = session;
            var available = availableToolNames
                .Where(name => _allowedTools.Contains(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new ResolvedToolPreset
            {
                PresetId = "test",
                Surface = "test",
                EffectiveAutonomyMode = "supervised",
                RequireToolApproval = false,
                AllowedTools = available,
                ApprovalRequiredTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            };
        }

        public IReadOnlyList<ResolvedToolPreset> ListPresets(IEnumerable<string> availableToolNames)
            => [Resolve(CreateSession("preset-list"), availableToolNames)];
    }

    private sealed class TestLlmExecutionService : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = options;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = AsyncEnumerable.Empty<ChatResponseUpdate>()
            });
        }
    }

    private sealed class CapturingLlmExecutionService : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public IReadOnlyList<string> LastNonStreamingToolNames { get; private set; } = [];

        public IReadOnlyList<string> LastStreamingToolNames { get; private set; } = [];

        public Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            LastNonStreamingToolNames = CaptureToolNames(options);
            return Task.FromResult(new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Response = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")])
            });
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = messages;
            _ = turnContext;
            _ = estimate;
            _ = ct;
            LastStreamingToolNames = CaptureToolNames(options);
            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = "maf-test-model",
                Updates = GetUpdates()
            });
        }

        private static IReadOnlyList<string> CaptureToolNames(ChatOptions options)
            => (options.Tools ?? [])
                .Select(static tool => tool.Name)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToArray();

        private static async IAsyncEnumerable<ChatResponseUpdate> GetUpdates()
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            await Task.CompletedTask;
        }
    }

    private sealed class MafTestChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = messages;
            _ = options;
            _ = cancellationToken;
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
#endif
