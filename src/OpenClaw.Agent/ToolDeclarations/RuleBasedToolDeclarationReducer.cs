using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.ToolDeclarations;

public sealed class RuleBasedToolDeclarationReducer : IToolDeclarationReducer
{
    private static readonly string[] HighRiskTools = ["shell", "process", "write_file", "code_exec"];

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = context.Config;
        var hardMax = Math.Max(1, config.HardMaxTools);
        var maxTools = Math.Clamp(config.MaxTools, 1, hardMax);
        var minTools = Math.Clamp(config.MinTools, 0, maxTools);
        var promptTokens = Tokenize(context.UserMessage ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateByName = context.CandidateTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var selected = new List<AITool>();
        var pinned = new List<string>();
        var skippedPinned = new List<string>();

        foreach (var requested in config.AlwaysIncludeTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            if (selected.Count >= hardMax)
            {
                skippedPinned.Add(requested);
                continue;
            }

            if (candidateByName.TryGetValue(requested.Trim(), out var tool) && selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            {
                selected.Add(tool);
                pinned.Add(tool.Name);
            }
            else
            {
                skippedPinned.Add(requested.Trim());
            }
        }

        var scores = context.CandidateTools
            .Where(tool => selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            .Select(tool => new ToolScore(tool, Score(tool, promptTokens, context)))
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var item in scores.Where(item => item.Score >= config.MinScore))
        {
            if (selected.Count >= maxTools || selected.Count >= hardMax)
                break;
            selected.Add(item.Tool);
        }

        foreach (var item in scores.Where(item => item.Score < config.MinScore))
        {
            if (selected.Count >= minTools || selected.Count >= maxTools || selected.Count >= hardMax)
                break;
            selected.Add(item.Tool);
        }

        var scoreMap = scores.ToDictionary(static item => item.Tool.Name, static item => item.Score, StringComparer.Ordinal);
        foreach (var tool in selected)
            scoreMap.TryAdd(tool.Name, pinned.Contains(tool.Name, StringComparer.Ordinal) ? 1.0 : 0.0);

        var diagnostics = new ToolDeclarationReductionDiagnostics
        {
            Enabled = true,
            Mode = "rule",
            CandidateCount = context.CandidateTools.Count,
            SelectedCount = selected.Count,
            MaxTools = maxTools,
            HardMaxTools = hardMax,
            PresetId = context.Preset?.PresetId,
            SelectedTools = selected.Select(static tool => tool.Name).ToArray(),
            PinnedTools = pinned,
            SkippedPinnedTools = skippedPinned,
            Scores = scoreMap
        };

        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = diagnostics
        });
    }

    private static double Score(AITool tool, HashSet<string> promptTokens, ToolDeclarationReductionContext context)
    {
        var score = 0.0;
        if (promptTokens.Contains(tool.Name))
            score += 1.0;

        var textTokens = Tokenize(ToolDeclarationText.Build(tool)).ToArray();
        score += textTokens.Count(promptTokens.Contains) * 0.12;

        if (context.RecentToolNames.Contains(tool.Name, StringComparer.Ordinal))
            score += 0.20;

        if (context.RecentToolFailures.TryGetValue(tool.Name, out var failures))
            score -= Math.Min(0.30, failures * 0.10);

        if (HighRiskTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) && !promptTokens.Contains(tool.Name))
            score -= 0.08;

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isTokenChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isTokenChar && start < 0)
                start = i;
            else if (!isTokenChar && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private sealed record ToolScore(AITool Tool, double Score);
}