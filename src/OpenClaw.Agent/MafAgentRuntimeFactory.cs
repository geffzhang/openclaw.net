using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenClaw.Agent;
using OpenClaw.Agent.Tools;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

public sealed class MafAgentRuntimeFactory : IAgentRuntimeFactory
{
    private readonly MafAgentFactory _agentFactory;
    private readonly MafSessionStateStore _sessionStateStore;
    private readonly MafTelemetryAdapter _telemetry;
    private readonly MafOptions _options;
    private readonly ILoggerFactory _loggerFactory;

    public MafAgentRuntimeFactory(
        MafAgentFactory agentFactory,
        MafSessionStateStore sessionStateStore,
        MafTelemetryAdapter telemetry,
        IOptions<MafOptions> options,
        ILoggerFactory loggerFactory)
    {
        _agentFactory = agentFactory;
        _sessionStateStore = sessionStateStore;
        _telemetry = telemetry;
        _options = options.Value;
        _loggerFactory = loggerFactory;
    }

    public string OrchestratorId => MafCapabilities.OrchestratorId;

    public IAgentRuntime Create(AgentRuntimeFactoryContext context)
    {
        MafCapabilities.EnsureSupported(context.RuntimeState);

        var runtimeContext = context;
        if (context.Config.Delegation.Enabled && context.Config.Delegation.Profiles.Count > 0)
            runtimeContext = AttachDelegateTool(context);

        return new MafAgentRuntime(
            runtimeContext,
            _options,
            _agentFactory,
            _sessionStateStore,
            _telemetry,
            _loggerFactory.CreateLogger("MafAgentRuntime"));
    }

    private AgentRuntimeFactoryContext AttachDelegateTool(AgentRuntimeFactoryContext context)
    {
        var delegateTool = new MafDelegateTool(
            context,
            _options,
            _agentFactory,
            _sessionStateStore,
            _telemetry,
            _loggerFactory.CreateLogger("MafDelegateTool"),
            currentDepth: 0);

        return new AgentRuntimeFactoryContext
        {
            Services = context.Services,
            Config = context.Config,
            RuntimeState = context.RuntimeState,
            ChatClient = context.ChatClient,
            Tools = [.. context.Tools, delegateTool],
            MemoryStore = context.MemoryStore,
            RuntimeMetrics = context.RuntimeMetrics,
            ProviderUsage = context.ProviderUsage,
            LlmExecutionService = context.LlmExecutionService,
            Skills = context.Skills,
            SkillsConfig = context.SkillsConfig,
            WorkspacePath = context.WorkspacePath,
            PluginSkillDirs = context.PluginSkillDirs,
            Logger = context.Logger,
            Hooks = context.Hooks,
            RequireToolApproval = context.RequireToolApproval,
            ApprovalRequiredTools = context.ApprovalRequiredTools
        };
    }
}
