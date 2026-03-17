using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Tools;

public sealed class MafDelegateTool : ITool
{
    private readonly AgentRuntimeFactoryContext _baseContext;
    private readonly MafOptions _options;
    private readonly MafAgentFactory _agentFactory;
    private readonly MafSessionStateStore _sessionStateStore;
    private readonly MafTelemetryAdapter _telemetry;
    private readonly ILogger? _logger;
    private readonly int _currentDepth;

    public string Name => "delegate_agent";

    public string Description =>
        "Delegate a subtask to a specialized sub-agent. " +
        "Available profiles: " + string.Join(", ", _baseContext.Config.Delegation.Profiles.Keys) + ". " +
        "Use this when a task requires a different expertise or focus area.";

    public string ParameterSchema =>
        """
        {
          "type": "object",
          "properties": {
            "profile": {
              "type": "string",
              "description": "Name of the agent profile to delegate to"
            },
            "task": {
              "type": "string",
              "description": "The task description for the sub-agent to complete"
            }
          },
          "required": ["profile", "task"]
        }
        """;

    public MafDelegateTool(
        AgentRuntimeFactoryContext baseContext,
        MafOptions options,
        MafAgentFactory agentFactory,
        MafSessionStateStore sessionStateStore,
        MafTelemetryAdapter telemetry,
        ILogger? logger = null,
        int currentDepth = 0)
    {
        _baseContext = baseContext;
        _options = options;
        _agentFactory = agentFactory;
        _sessionStateStore = sessionStateStore;
        _telemetry = telemetry;
        _logger = logger;
        _currentDepth = currentDepth;
    }

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        var (profileName, task) = ParseArguments(argumentsJson);

        if (string.IsNullOrWhiteSpace(profileName))
            return "Error: 'profile' parameter is required.";
        if (string.IsNullOrWhiteSpace(task))
            return "Error: 'task' parameter is required.";

        var delegation = _baseContext.Config.Delegation;
        if (!delegation.Profiles.TryGetValue(profileName, out var profile))
            return $"Error: Unknown agent profile '{profileName}'. Available: {string.Join(", ", delegation.Profiles.Keys)}";

        if (_currentDepth >= delegation.MaxDepth)
            return $"Error: Maximum delegation depth ({delegation.MaxDepth}) reached. Cannot delegate further.";

        _logger?.LogInformation(
            "Delegating to MAF sub-agent '{Profile}' (depth {Depth}): {Task}",
            profileName,
            _currentDepth + 1,
            Truncate(task, 100));

        var toolSubset = BuildToolSubset(profile);
        var childContext = CreateChildContext(toolSubset);
        var subRuntime = new MafAgentRuntime(
            childContext,
            _options,
            _agentFactory,
            _sessionStateStore,
            _telemetry,
            _logger,
            persistSessionState: false);

        var subSession = new Session
        {
            Id = $"delegate:{profileName}:{Guid.NewGuid():N}",
            ChannelId = "delegation",
            SenderId = profileName
        };

        var fullTask = BuildDelegatedTask(task, profile);

        try
        {
            var result = await subRuntime.RunAsync(subSession, fullTask, ct);
            _logger?.LogInformation(
                "MAF sub-agent '{Profile}' completed (depth {Depth}), response length={Length}",
                profileName,
                _currentDepth + 1,
                result.Length);
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "MAF sub-agent '{Profile}' failed (depth {Depth})", profileName, _currentDepth + 1);
            return $"Error: Sub-agent '{profileName}' failed: {ex.Message}";
        }
    }

    private static (string? ProfileName, string? Task) ParseArguments(string argumentsJson)
    {
        using var doc = JsonDocument.Parse(argumentsJson);
        var root = doc.RootElement;
        var profileName = root.TryGetProperty("profile", out var p) ? p.GetString() : null;
        var task = root.TryGetProperty("task", out var t) ? t.GetString() : null;
        return (profileName, task);
    }

    private IReadOnlyList<ITool> BuildToolSubset(AgentProfile profile)
    {
        var tools = profile.AllowedTools.Length > 0
            ? _baseContext.Tools
                .Where(tool => tool.Name != Name && profile.AllowedTools.Contains(tool.Name, StringComparer.Ordinal))
                .ToList()
            : _baseContext.Tools
                .Where(tool => tool.Name != Name)
                .ToList();

        if (_currentDepth + 1 < _baseContext.Config.Delegation.MaxDepth)
        {
            tools.Add(new MafDelegateTool(
                _baseContext,
                _options,
                _agentFactory,
                _sessionStateStore,
                _telemetry,
                _logger,
                _currentDepth + 1));
        }

        return tools;
    }

    private AgentRuntimeFactoryContext CreateChildContext(IReadOnlyList<ITool> tools)
        => new()
        {
            Services = _baseContext.Services,
            Config = _baseContext.Config,
            RuntimeState = _baseContext.RuntimeState,
            ChatClient = _baseContext.ChatClient,
            Tools = tools,
            MemoryStore = _baseContext.MemoryStore,
            RuntimeMetrics = _baseContext.RuntimeMetrics,
            ProviderUsage = _baseContext.ProviderUsage,
            LlmExecutionService = _baseContext.LlmExecutionService,
            Skills = _baseContext.Skills,
            SkillsConfig = _baseContext.SkillsConfig,
            WorkspacePath = _baseContext.WorkspacePath,
            PluginSkillDirs = _baseContext.PluginSkillDirs,
            Logger = _baseContext.Logger,
            Hooks = _baseContext.Hooks,
            RequireToolApproval = _baseContext.RequireToolApproval,
            ApprovalRequiredTools = _baseContext.ApprovalRequiredTools
        };

    private static string BuildDelegatedTask(string task, AgentProfile profile)
        => string.IsNullOrWhiteSpace(profile.SystemPrompt)
            ? task
            : $"[Context: {profile.SystemPrompt}]\n\n{task}";

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "…";
}
