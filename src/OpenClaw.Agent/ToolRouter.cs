using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

public sealed class ToolRouter : IToolRouter
{
    private readonly IToolIndex _index;
    private readonly ILogger? _logger;

    public ToolRouter(IToolIndex index, ILogger<ToolRouter>? logger = null)
    {
        _index = index;
        _logger = logger;
    }

    public async ValueTask<IReadOnlyList<ToolRouteCandidate>> RouteAsync(
        string prompt,
        IReadOnlyList<ToolDefinitionSnapshot> tools,
        IReadOnlyCollection<string> candidateToolNames,
        ToolSemanticRoutingConfig config,
        CancellationToken ct)
    {
        if (!config.Enabled || candidateToolNames.Count <= config.TopK)
            return candidateToolNames.Select(static name => new ToolRouteCandidate(name, 1f)).ToArray();

        if (string.IsNullOrWhiteSpace(prompt))
            return TakeFallback(candidateToolNames, config.TopK);

        try
        {
            await _index.InitializeAsync(tools, ct);
            var routed = await _index.SearchAsync(
                prompt,
                candidateToolNames,
                config.TopK,
                config.MinScore,
                config.Mode,
                ct);

            if (routed.Count > 0)
                return routed;

            return config.IncludeFallbackTools
                ? TakeFallback(candidateToolNames, config.TopK)
                : [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (config.FailOpen)
        {
            _logger?.LogWarning(
                ex,
                "Tool semantic routing failed open. Returning {Count} preset-filtered tools.",
                candidateToolNames.Count);
            return candidateToolNames.Select(static name => new ToolRouteCandidate(name, 1f)).ToArray();
        }
    }

    private static IReadOnlyList<ToolRouteCandidate> TakeFallback(
        IReadOnlyCollection<string> candidateToolNames,
        int topK)
        => candidateToolNames
            .Take(topK)
            .Select(static name => new ToolRouteCandidate(name, 0f))
            .ToArray();
}
