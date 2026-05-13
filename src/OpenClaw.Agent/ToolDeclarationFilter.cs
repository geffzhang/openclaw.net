using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent;

public sealed class ToolDeclarationFilter : IToolDeclarationFilter
{
    private readonly ToolSemanticRoutingConfig _config;
    private readonly IToolRouter _router;
    private readonly ILogger? _logger;

    public ToolDeclarationFilter(
        ToolSemanticRoutingConfig config,
        IToolRouter router,
        ILogger<ToolDeclarationFilter>? logger = null)
    {
        _config = config;
        _router = router;
        _logger = logger;
    }

    public async ValueTask<IReadOnlyList<string>> FilterToolNamesAsync(
        Session session,
        string userPrompt,
        IReadOnlyList<ToolDefinitionSnapshot> tools,
        IReadOnlyCollection<string> candidateToolNames,
        CancellationToken ct)
    {
        _ = session;

        if (!_config.Enabled || candidateToolNames.Count <= _config.TopK)
            return candidateToolNames.ToArray();

        var routed = await _router.RouteAsync(userPrompt, tools, candidateToolNames, _config, ct);
        var selected = routed
            .Select(static candidate => candidate.ToolName)
            .Where(candidateToolNames.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        _logger?.LogDebug(
            "Tool semantic routing selected {SelectedCount}/{CandidateCount} tools. topK={TopK} mode={Mode}",
            selected.Length,
            candidateToolNames.Count,
            _config.TopK,
            ToolSemanticRoutingModes.Normalize(_config.Mode));

        return selected;
    }
}
