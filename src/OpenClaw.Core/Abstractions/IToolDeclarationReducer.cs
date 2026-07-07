using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolDeclarationReducer
{
    ValueTask<ToolDeclarationReductionResult> ReduceAsync(
        ToolDeclarationReductionContext context,
        CancellationToken ct);
}

public sealed class ToolDeclarationReductionRequest
{
    public IReadOnlyList<string> RecentToolNames { get; init; } = [];
    public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public bool IsTurnRoutingProbe { get; init; }
}

public sealed class ToolDeclarationReductionContext
{
    public required Session Session { get; init; }
    public string? UserMessage { get; init; }
    public required IReadOnlyList<AITool> CandidateTools { get; init; }
    public ResolvedToolPreset? Preset { get; init; }
    public required ToolDeclarationReductionConfig Config { get; init; }
    public IReadOnlyList<string> RecentToolNames { get; init; } = [];
    public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public bool IsTurnRoutingProbe { get; init; }
}

public sealed class ToolDeclarationReductionResult
{
    public required IReadOnlyList<AITool> Tools { get; init; }
    public required ToolDeclarationReductionDiagnostics Diagnostics { get; init; }
}

public sealed class ToolDeclarationReductionDiagnostics
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "off";
    public int CandidateCount { get; init; }
    public int SelectedCount { get; init; }
    public int MaxTools { get; init; }
    public int HardMaxTools { get; init; }
    public string? PresetId { get; init; }
    public bool FallbackUsed { get; init; }
    public string? FallbackReason { get; init; }
    public IReadOnlyList<string> SelectedTools { get; init; } = [];
    public IReadOnlyList<string> PinnedTools { get; init; } = [];
    public IReadOnlyList<string> SkippedPinnedTools { get; init; } = [];
    public IReadOnlyDictionary<string, double> Scores { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}