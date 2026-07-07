using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.ToolDeclarations;

public sealed class RuleBasedToolDeclarationReducer : IToolDeclarationReducer
{
    private static readonly string[] HighRiskTools = ["shell", "process", "write_file", "code_exec"];
    private static readonly StringComparer ToolNameComparer = StringComparer.Ordinal;

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = context.Config;
        var hardMax = Math.Max(1, config.HardMaxTools);
        var maxTools = Math.Clamp(config.MaxTools, 1, hardMax);
        var minTools = Math.Clamp(config.MinTools, 0, maxTools);
        if (context.Session.RouteToolsDisabled)
            return ValueTask.FromResult(CreateResult(context, [], [], [], [], maxTools, hardMax));

        var allowedCandidates = context.CandidateTools
            .Where(tool => IsAllowedByRoute(tool.Name, context.Session.RouteAllowedTools))
            .Where(tool => IsAllowedByPreset(tool.Name, context.Preset))
            .ToArray();
        var neverAutoInclude = config.NeverAutoIncludeTools
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .ToHashSet(ToolNameComparer);
        var promptTokens = Tokenize(context.UserMessage ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateByName = allowedCandidates.ToDictionary(static tool => tool.Name, ToolNameComparer);
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

            if (candidateByName.TryGetValue(requested.Trim(), out var tool) && selected.All(existing => !ToolNameComparer.Equals(existing.Name, tool.Name)))
            {
                selected.Add(tool);
                pinned.Add(tool.Name);
            }
            else
            {
                skippedPinned.Add(requested.Trim());
            }
        }

        var scores = allowedCandidates
            .Where(tool => !neverAutoInclude.Contains(tool.Name))
            .Where(tool => selected.All(existing => !ToolNameComparer.Equals(existing.Name, tool.Name)))
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

        return ValueTask.FromResult(CreateResult(context, selected, pinned, skippedPinned, scores, maxTools, hardMax));
    }

    private static ToolDeclarationReductionResult CreateResult(
        ToolDeclarationReductionContext context,
        IReadOnlyList<AITool> selected,
        IReadOnlyList<string> pinned,
        IReadOnlyList<string> skippedPinned,
        IReadOnlyList<ToolScore> scores,
        int maxTools,
        int hardMax)
    {
        var scoreMap = scores.ToDictionary(static item => item.Tool.Name, static item => item.Score, StringComparer.Ordinal);
        foreach (var tool in selected)
            scoreMap.TryAdd(tool.Name, pinned.Contains(tool.Name, ToolNameComparer) ? 1.0 : 0.0);

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

        return new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = diagnostics
        };
    }

    private static bool IsAllowedByRoute(string toolName, string[] routeAllowedTools)
    {
        return routeAllowedTools.Length == 0 || routeAllowedTools.Contains(toolName, ToolNameComparer);
    }

    private static bool IsAllowedByPreset(string toolName, ResolvedToolPreset? preset)
    {
        return preset?.AllowedTools.Count is not > 0 || preset.AllowedTools.Any(allowedTool => string.Equals(allowedTool, toolName, StringComparison.Ordinal));
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

        if (HighRiskTools.Contains(tool.Name, StringComparer.Ordinal) && !promptTokens.Contains(tool.Name))
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