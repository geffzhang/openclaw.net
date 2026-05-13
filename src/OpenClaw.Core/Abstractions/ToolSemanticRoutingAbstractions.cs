using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public sealed class ToolSemanticRoutingConfig
{
    public bool Enabled { get; set; } = false;
    public int TopK { get; set; } = 12;
    public float MinScore { get; set; } = 0f;
    public string Mode { get; set; } = ToolSemanticRoutingModes.Balanced;
    public bool IncludeFallbackTools { get; set; } = true;
    public int QueryCacheSize { get; set; } = 128;
    public string ToolTextMode { get; set; } = ToolSemanticRoutingToolTextModes.SchemaSummary;
    public string? EmbeddingProvider { get; set; }
    public string EmbeddingModel { get; set; } = "sentence-transformers/all-MiniLM-L6-v2";
    public string? ModelPath { get; set; }
    public string? CacheDirectory { get; set; }
    public int MaxSequenceLength { get; set; } = 512;
    public bool NormalizeEmbeddings { get; set; } = false;
    public bool PreferQuantized { get; set; } = false;
    public bool EnsureModelDownloaded { get; set; } = true;
    public bool FailOpen { get; set; } = true;
}

public static class ToolSemanticRoutingEmbeddingProviders
{
    public const string Onnx = "onnx";

    public static string? Normalize(string? provider)
        => string.IsNullOrWhiteSpace(provider) ? null : provider.Trim().ToLowerInvariant();
}

public static class ToolSemanticRoutingModes
{
    public const string Fast = "fast";
    public const string Balanced = "balanced";
    public const string Accurate = "accurate";

    public static string Normalize(string? mode)
        => (mode ?? Balanced).Trim().ToLowerInvariant();
}

public static class ToolSemanticRoutingToolTextModes
{
    public const string NameDescription = "name-description";
    public const string SchemaSummary = "schema-summary";
    public const string FullSchema = "full-schema";

    public static string Normalize(string? mode)
        => (mode ?? SchemaSummary).Trim().ToLowerInvariant();
}

public sealed record ToolDefinitionSnapshot(
    string Name,
    string Description,
    string ParameterSchema,
    string EmbeddingText,
    string DefinitionHash);

public sealed record ToolRouteCandidate(
    string ToolName,
    float Score);

public interface IToolIndex
{
    long Revision { get; }

    ValueTask InitializeAsync(IEnumerable<ToolDefinitionSnapshot> tools, CancellationToken ct);

    ValueTask AddOrUpdateToolAsync(ToolDefinitionSnapshot tool, CancellationToken ct);

    bool RemoveTool(string toolName);

    ValueTask<IReadOnlyList<ToolRouteCandidate>> SearchAsync(
        string prompt,
        IReadOnlyCollection<string> candidateToolNames,
        int topK,
        float minScore,
        string mode,
        CancellationToken ct);

    void ClearCache();
}

public interface IToolRouter
{
    ValueTask<IReadOnlyList<ToolRouteCandidate>> RouteAsync(
        string prompt,
        IReadOnlyList<ToolDefinitionSnapshot> tools,
        IReadOnlyCollection<string> candidateToolNames,
        ToolSemanticRoutingConfig config,
        CancellationToken ct);
}

public interface IToolDeclarationFilter
{
    ValueTask<IReadOnlyList<string>> FilterToolNamesAsync(
        Session session,
        string userPrompt,
        IReadOnlyList<ToolDefinitionSnapshot> tools,
        IReadOnlyCollection<string> candidateToolNames,
        CancellationToken ct);
}
