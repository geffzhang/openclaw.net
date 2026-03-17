using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Core.Skills;
using System.Reflection;

namespace OpenClaw.Tests;

internal static class MafTestRuntimeFactory
{
    public static MafAgentRuntime CreateRuntime(
        IChatClient chatClient,
        IMemoryStore memoryStore,
        IReadOnlyList<ITool>? tools = null,
        LlmProviderConfig? llmConfig = null,
        int maxHistoryTurns = 10,
        bool enableStreaming = true,
        bool enableCompaction = false,
        int compactionThreshold = 40,
        int compactionKeepRecent = 10,
        bool parallelToolExecution = true,
        bool requireToolApproval = false,
        IReadOnlyList<string>? approvalRequiredTools = null,
        IReadOnlyList<IToolHook>? hooks = null,
        MemoryRecallConfig? recall = null,
        SkillsConfig? skillsConfig = null,
        string? skillWorkspacePath = null,
        IReadOnlyList<SkillDefinition>? skills = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new MafOptions { EnableStreaming = enableStreaming });
        var config = new GatewayConfig
        {
            Runtime = new RuntimeConfig { Mode = "jit", Orchestrator = RuntimeOrchestrator.Maf },
            Llm = llmConfig ?? new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            Memory = new MemoryConfig
            {
                StoragePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                MaxHistoryTurns = maxHistoryTurns,
                EnableCompaction = enableCompaction,
                CompactionThreshold = compactionThreshold,
                CompactionKeepRecent = compactionKeepRecent,
                Recall = recall ?? new MemoryRecallConfig()
            },
            Tooling = new ToolingConfig
            {
                ParallelToolExecution = parallelToolExecution,
                RequireToolApproval = requireToolApproval,
                ApprovalRequiredTools = [.. approvalRequiredTools ?? []]
            },
            Skills = skillsConfig ?? new SkillsConfig()
        };

        var telemetry = new MafTelemetryAdapter();
        var sessionStateStore = new MafSessionStateStore(config, options, NullLogger<MafSessionStateStore>.Instance);
        var agentFactory = new MafAgentFactory(options, NullLoggerFactory.Instance, services);
        var context = new AgentRuntimeFactoryContext
        {
            Services = services,
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = config.Runtime.Mode,
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            ChatClient = chatClient,
            Tools = tools ?? [],
            MemoryStore = memoryStore,
            RuntimeMetrics = new RuntimeMetrics(),
            ProviderUsage = new ProviderUsageTracker(),
            LlmExecutionService = new PassThroughLlmExecutionService(chatClient, config.Llm.RetryCount),
            Skills = skills ?? [],
            SkillsConfig = config.Skills,
            WorkspacePath = skillWorkspacePath,
            PluginSkillDirs = [],
            Logger = NullLogger.Instance,
            Hooks = hooks ?? [],
            RequireToolApproval = requireToolApproval,
            ApprovalRequiredTools = approvalRequiredTools ?? []
        };

        return new MafAgentRuntime(
            context,
            options.Value,
            agentFactory,
            sessionStateStore,
            telemetry,
            NullLogger.Instance,
            persistSessionState: false);
    }

    public static Task CompactHistoryAsync(MafAgentRuntime runtime, Session session, CancellationToken ct)
    {
        var method = typeof(MafAgentRuntime).GetMethod("CompactHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(MafAgentRuntime).FullName, "CompactHistoryAsync");
        return (Task)(method.Invoke(runtime, [session, ct])
            ?? throw new InvalidOperationException("CompactHistoryAsync invocation returned null."));
    }

    public static MafDelegateTool CreateDelegateTool(
        IChatClient chatClient,
        IMemoryStore memoryStore,
        DelegationConfig delegationConfig,
        IReadOnlyList<ITool>? tools = null,
        LlmProviderConfig? llmConfig = null,
        int currentDepth = 0,
        bool requireToolApproval = false,
        IReadOnlyList<string>? approvalRequiredTools = null,
        IReadOnlyList<IToolHook>? hooks = null,
        MemoryRecallConfig? recall = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = Options.Create(new MafOptions());
        var config = new GatewayConfig
        {
            Runtime = new RuntimeConfig { Mode = "jit", Orchestrator = RuntimeOrchestrator.Maf },
            Llm = llmConfig ?? new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" },
            Memory = new MemoryConfig
            {
                StoragePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                Recall = recall ?? new MemoryRecallConfig()
            },
            Tooling = new ToolingConfig
            {
                RequireToolApproval = requireToolApproval,
                ApprovalRequiredTools = [.. approvalRequiredTools ?? []]
            },
            Delegation = delegationConfig,
            Skills = new SkillsConfig()
        };

        var telemetry = new MafTelemetryAdapter();
        var sessionStateStore = new MafSessionStateStore(config, options, NullLogger<MafSessionStateStore>.Instance);
        var agentFactory = new MafAgentFactory(options, NullLoggerFactory.Instance, services);
        var context = new AgentRuntimeFactoryContext
        {
            Services = services,
            Config = config,
            RuntimeState = new GatewayRuntimeState
            {
                RequestedMode = config.Runtime.Mode,
                EffectiveMode = GatewayRuntimeMode.Jit,
                DynamicCodeSupported = true
            },
            ChatClient = chatClient,
            Tools = tools ?? [],
            MemoryStore = memoryStore,
            RuntimeMetrics = new RuntimeMetrics(),
            ProviderUsage = new ProviderUsageTracker(),
            LlmExecutionService = new PassThroughLlmExecutionService(chatClient, config.Llm.RetryCount),
            Skills = [],
            SkillsConfig = config.Skills,
            WorkspacePath = null,
            PluginSkillDirs = [],
            Logger = NullLogger.Instance,
            Hooks = hooks ?? [],
            RequireToolApproval = requireToolApproval,
            ApprovalRequiredTools = approvalRequiredTools ?? []
        };

        return new MafDelegateTool(
            context,
            options.Value,
            agentFactory,
            sessionStateStore,
            telemetry,
            NullLogger.Instance,
            currentDepth);
    }

    private sealed class PassThroughLlmExecutionService(IChatClient chatClient, int retryCount) : ILlmExecutionService
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
            Exception? lastError = null;
            for (var attempt = 0; attempt <= Math.Max(0, retryCount); attempt++)
            {
                try
                {
                    return new LlmExecutionResult
                    {
                        ProviderId = "test",
                        ModelId = options.ModelId ?? "test-model",
                        Response = await chatClient.GetResponseAsync(messages, options, ct)
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt >= Math.Max(0, retryCount))
                        throw;
                }
            }

            throw lastError ?? new InvalidOperationException("No LLM response was produced.");
        }

        public Task<LlmStreamingExecutionResult> StartStreamingAsync(
            Session session,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions options,
            TurnContext turnContext,
            LlmExecutionEstimate estimate,
            CancellationToken ct)
            => Task.FromResult(new LlmStreamingExecutionResult
            {
                ProviderId = "test",
                ModelId = options.ModelId ?? "test-model",
                Updates = chatClient.GetStreamingResponseAsync(messages, options, ct)
            });
    }
}
