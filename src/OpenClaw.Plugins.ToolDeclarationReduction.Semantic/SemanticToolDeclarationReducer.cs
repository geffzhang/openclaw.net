using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

public sealed class SemanticToolDeclarationReducer(ILogger logger) : IToolDeclarationReducer
{
    private readonly object _gate = new();
    private SemanticToolIndex? _index;

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(
        ToolDeclarationReductionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = context.Config;
        var hardMax = Math.Max(1, config.HardMaxTools);
        var maxTools = Math.Clamp(config.MaxTools, 1, hardMax);
        var minTools = Math.Clamp(config.MinTools, 0, maxTools);
        var minScore = Math.Clamp(config.MinScore, 0.0, 1.0);
        var neverAutoInclude = new HashSet<string>(config.NeverAutoIncludeTools.Where(static item => !string.IsNullOrWhiteSpace(item)).Select(static item => item.Trim()), StringComparer.Ordinal);
        var candidates = context.CandidateTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var index = GetOrBuildIndex(context.CandidateTools);

        MergeScores(scores, index.Search(context.UserMessage ?? string.Empty, hardMax, minScore));
        if (config.EnablePromptDistillation)
        {
            foreach (var phrase in PromptIntentDistiller.DistillActionPhrases(context.UserMessage ?? string.Empty))
                MergeScores(scores, index.Search(phrase, hardMax, minScore), 0.92);
        }

        var selected = new List<AITool>();
        var pinned = new List<string>();
        var skippedPinned = new List<string>();

        foreach (var requested in config.AlwaysIncludeTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            var toolName = requested.Trim();
            if (selected.Count >= hardMax)
            {
                skippedPinned.Add(toolName);
                continue;
            }

            if (candidates.TryGetValue(toolName, out var tool))
            {
                selected.Add(tool);
                pinned.Add(tool.Name);
                scores.TryAdd(tool.Name, 1.0);
            }
            else
            {
                skippedPinned.Add(toolName);
            }
        }

        var ranked = context.CandidateTools
            .Where(tool => !selected.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            .Where(tool => !neverAutoInclude.Contains(tool.Name))
            .Select(tool => new RankedTool(tool, IsHybrid(config.Mode)
                ? (0.45 * LexicalScore(tool, context.UserMessage) + 0.55 * scores.GetValueOrDefault(tool.Name))
                : scores.GetValueOrDefault(tool.Name)))
            .Where(item => item.Score >= minScore)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, maxTools - selected.Count));

        foreach (var item in ranked)
        {
            selected.Add(item.Tool);
            scores[item.Tool.Name] = item.Score;
        }

        if (selected.Count < minTools)
        {
            foreach (var tool in context.CandidateTools)
            {
                if (selected.Count >= minTools)
                    break;

                if (selected.Any(existing => string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
                    continue;

                if (neverAutoInclude.Contains(tool.Name))
                    continue;

                selected.Add(tool);
                scores.TryAdd(tool.Name, 0.0);
            }
        }

        var fallbackUsed = selected.Count == 0 && context.CandidateTools.Count > 0;
        logger.LogDebug("Semantic tool declaration reduction selected {SelectedCount} of {CandidateCount} tools.", selected.Count, context.CandidateTools.Count);

        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = new ToolDeclarationReductionDiagnostics
            {
                Enabled = true,
                Mode = IsHybrid(config.Mode) ? "hybrid" : "semantic",
                CandidateCount = context.CandidateTools.Count,
                SelectedCount = selected.Count,
                MaxTools = maxTools,
                HardMaxTools = hardMax,
                PresetId = context.Preset?.PresetId,
                FallbackUsed = fallbackUsed,
                FallbackReason = fallbackUsed ? "semantic_no_results" : null,
                SelectedTools = selected.Select(static tool => tool.Name).ToArray(),
                PinnedTools = pinned,
                SkippedPinnedTools = skippedPinned,
                Scores = scores
            }
        });
    }

    private SemanticToolIndex GetOrBuildIndex(IReadOnlyList<AITool> tools)
    {
        var rebuilt = SemanticToolIndex.Build(tools);
        lock (_gate)
        {
            if (_index is null || !string.Equals(_index.Fingerprint, rebuilt.Fingerprint, StringComparison.Ordinal))
                _index = rebuilt;

            return _index;
        }
    }

    private static void MergeScores(
        IDictionary<string, double> scores,
        IReadOnlyList<SemanticToolSearchResult> results,
        double discount = 1.0)
    {
        foreach (var result in results)
        {
            var score = result.Score * discount;
            if (!scores.TryGetValue(result.Tool.Name, out var existing) || score > existing)
                scores[result.Tool.Name] = score;
        }
    }

    private static double LexicalScore(AITool tool, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return 0.0;

        if (prompt.Contains(tool.Name, StringComparison.OrdinalIgnoreCase))
            return 1.0;

        var description = tool.Description ?? string.Empty;
        return prompt.Contains(description, StringComparison.OrdinalIgnoreCase) ? 0.5 : 0.0;
    }

    private static bool IsHybrid(string? mode)
        => string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase);

    private sealed record RankedTool(AITool Tool, double Score);
}