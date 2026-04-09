using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Memory;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Sessions;
using OpenClaw.Evaluation.Models;
using OpenClaw.Evaluation.Telemetry;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using OpenTelemetry.Trace;
using Xunit;

namespace OpenClaw.AgentEval.Tests.Infrastructure;

public sealed class OpenClawEvaluationHarness : IAsyncLifetime
{
    private readonly string _storagePath = Path.Combine(
        Path.GetTempPath(),
        "openclaw-agenteval-tests",
        Guid.NewGuid().ToString("N"));

    public IHost AppHost { get; private set; } = null!;

    public List<Activity> InterceptedActivities { get; } = [];

    public SessionManager SessionManager => AppHost.Services.GetRequiredService<SessionManager>();

    public IAgentRuntime Runtime => AppHost.Services.GetRequiredService<IAgentRuntime>();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_storagePath);

        AppHost = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                var config = CreateConfig(_storagePath);
                var runtimeState = RuntimeModeResolver.Resolve(config.Runtime, dynamicCodeSupported: true);

                services.AddLogging();
                services.AddOptions();
                services.AddSingleton(config);
                services.AddSingleton(runtimeState);
                services.AddSingleton<RuntimeMetrics>();
                services.AddSingleton<ProviderUsageTracker>();
                services.AddSingleton<IMemoryStore>(_ => new FileMemoryStore(_storagePath, maxCachedSessions: 8));
                services.AddSingleton(sp => new SessionManager(
                    sp.GetRequiredService<IMemoryStore>(),
                    config,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentEval.SessionManager"),
                    sp.GetRequiredService<RuntimeMetrics>()));
                services.AddSingleton<IChatClient, FlightAssistantChatClient>();
                services.AddSingleton<ILlmExecutionService, FlightAssistantLlmExecutionService>();
                services.AddSingleton<ITool, SearchFlightsTool>();
                services.AddSingleton<ITool, BookFlightTool>();
                services.AddSingleton<ToolUsageTracker>();
                services.AddOpenTelemetry().WithTracing(tracing =>
                {
                    tracing.AddSource(Telemetry.ServiceName);
                    tracing.AddInMemoryExporter(InterceptedActivities);
                });
                services.AddMicrosoftAgentFrameworkExperiment(context.Configuration);
                services.AddSingleton<IAgentRuntime>(sp => CreateRuntime(sp, config, runtimeState));
            })
            .Build();

        return AppHost.StartAsync();
    }

    public async Task<string> RunAsync(string sessionId, string userMessage, CancellationToken ct = default)
    {
        InterceptedActivities.Clear();
        var session = await SessionManager.GetOrCreateByIdAsync(sessionId, "eval", "user1", ct);
        return await Runtime.RunAsync(session, userMessage, ct);
    }

    public AgentExecutionTrace ExtractExecutionTrace(string sessionId)
        => ActivityTraceExtractor.ExtractTrace(InterceptedActivities, sessionId);

    public async Task DisposeAsync()
    {
        if (AppHost is not null)
            await AppHost.StopAsync();

        AppHost?.Dispose();

        if (Directory.Exists(_storagePath))
            Directory.Delete(_storagePath, recursive: true);
    }

    private static IAgentRuntime CreateRuntime(
        IServiceProvider services,
        GatewayConfig config,
        GatewayRuntimeState runtimeState)
    {
        var factory = AgentRuntimeFactorySelector.Select(
            services.GetServices<IAgentRuntimeFactory>(),
            config.Runtime.Orchestrator);

        return factory.Create(new AgentRuntimeFactoryContext
        {
            Services = services,
            Config = config,
            RuntimeState = runtimeState,
            ChatClient = services.GetRequiredService<IChatClient>(),
            Tools = services.GetServices<ITool>().ToArray(),
            MemoryStore = services.GetRequiredService<IMemoryStore>(),
            RuntimeMetrics = services.GetRequiredService<RuntimeMetrics>(),
            ProviderUsage = services.GetRequiredService<ProviderUsageTracker>(),
            LlmExecutionService = services.GetRequiredService<ILlmExecutionService>(),
            Skills = [],
            SkillsConfig = config.Skills,
            WorkspacePath = null,
            PluginSkillDirs = [],
            Logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentEval.MafRuntime"),
            Hooks = [],
            RequireToolApproval = false,
            ApprovalRequiredTools = [],
            ToolUsageTracker = services.GetRequiredService<ToolUsageTracker>(),
            IsContractTokenBudgetExceeded = null,
            IsContractRuntimeBudgetExceeded = null,
            RecordContractTurnUsage = null,
            AppendContractSnapshot = null
        });
    }

    private static GatewayConfig CreateConfig(string storagePath)
        => new()
        {
            Memory = new MemoryConfig
            {
                StoragePath = storagePath,
                MaxHistoryTurns = 12
            },
            Llm = new LlmProviderConfig
            {
                Provider = "test-maf",
                Model = "gpt-4o-eval",
                ApiKey = "test-key"
            },
            Runtime = new RuntimeConfig
            {
                Mode = "jit",
                Orchestrator = RuntimeOrchestrator.Maf
            },
            Tooling = new ToolingConfig
            {
                EnableBrowserTool = false,
                ToolTimeoutSeconds = 10
            }
        };

    private sealed class FlightAssistantLlmExecutionService(IChatClient chatClient) : ILlmExecutionService
    {
        public CircuitState DefaultCircuitState => CircuitState.Closed;

        public async Task<LlmExecutionResult> GetResponseAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
        {
            _ = session;
            _ = turnContext;
            _ = estimate;

            return new LlmExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = options.ModelId ?? "gpt-4o-eval",
                Response = await chatClient.GetResponseAsync(messages, options, ct)
            };
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
            _ = turnContext;
            _ = estimate;

            return Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test-maf",
                ModelId = options.ModelId ?? "gpt-4o-eval",
                Updates = chatClient.GetStreamingResponseAsync(messages, options, ct)
            });
        }
    }

    private sealed class FlightAssistantChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            _ = cancellationToken;

            var messageList = messages.ToList();
            var toolMessageCount = messageList.Count(static message => message.Role == ChatRole.Tool);

            if (toolMessageCount == 0)
            {
                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, new AIContent[]
                    {
                        new FunctionCallContent(
                            "call_search_1",
                            "SearchFlights",
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["origin"] = "Beijing",
                                ["destination"] = "Tokyo",
                                ["departureDate"] = "next-monday"
                            })
                    })
                ]));
            }

            if (toolMessageCount == 1)
            {
                return Task.FromResult(new ChatResponse(
                [
                    new ChatMessage(ChatRole.Assistant, new AIContent[]
                    {
                        new FunctionCallContent(
                            "call_book_1",
                            "BookFlight",
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["flightId"] = "OC-CA123",
                                ["fareClass"] = "economy-flex"
                            })
                    })
                ]));
            }

            return Task.FromResult(new ChatResponse(
            [
                new ChatMessage(ChatRole.Assistant, "已完成搜寻并预订最优航班。")
            ]));
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

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            _ = serviceType;
            _ = serviceKey;
            return null;
        }

        public void Dispose()
        {
        }
    }

    private sealed class SearchFlightsTool : ITool
    {
        public string Name => "SearchFlights";

        public string Description => "Searches available flights for a requested route and date.";

        public string ParameterSchema =>
            """{"type":"object","properties":{"origin":{"type":"string"},"destination":{"type":"string"},"departureDate":{"type":"string"}},"required":["origin","destination","departureDate"]}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = ct;
            using var payload = JsonDocument.Parse(argumentsJson);
            var origin = payload.RootElement.GetProperty("origin").GetString();
            var destination = payload.RootElement.GetProperty("destination").GetString();
            return ValueTask.FromResult($"最佳航班 OC-CA123：{origin} -> {destination}，余票充足。");
        }
    }

    private sealed class BookFlightTool : ITool
    {
        public string Name => "BookFlight";

        public string Description => "Books the selected flight returned by the search step.";

        public string ParameterSchema =>
            """{"type":"object","properties":{"flightId":{"type":"string"},"fareClass":{"type":"string"}},"required":["flightId"]}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = ct;
            using var payload = JsonDocument.Parse(argumentsJson);
            var flightId = payload.RootElement.GetProperty("flightId").GetString();
            flightId.Should().NotBeNullOrWhiteSpace();
            return ValueTask.FromResult($"已预订航班 {flightId}，订单号 BOOK-0001。");
        }
    }
}
