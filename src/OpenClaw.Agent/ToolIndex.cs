using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

public sealed class ToolIndex : IToolIndex
{
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly ToolSemanticRoutingConfig _config;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ToolDefinitionSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]> _embeddings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ToolRouteCandidate>> _queryCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _queryCacheOrder = new();
    private long _revision;

    public ToolIndex(
        ToolSemanticRoutingConfig config,
        IEmbeddingGenerator<string, Embedding<float>>? embeddingGenerator = null,
        ILogger<ToolIndex>? logger = null)
    {
        _embeddingGenerator = embeddingGenerator;
        _config = config;
        _logger = logger;
    }

    public long Revision => Interlocked.Read(ref _revision);

    public async ValueTask InitializeAsync(IEnumerable<ToolDefinitionSnapshot> tools, CancellationToken ct)
    {
        var toolList = tools.ToArray();
        EnsureEmbeddingGenerator();

        await _gate.WaitAsync(ct);
        try
        {
            var currentNames = toolList.Select(static tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in _snapshots.Keys.Where(name => !currentNames.Contains(name)).ToArray())
            {
                _snapshots.Remove(removed);
                _embeddings.Remove(removed);
                BumpRevisionAndClearCache();
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var tool in toolList)
            await AddOrUpdateToolAsync(tool, ct);
    }

    public async ValueTask AddOrUpdateToolAsync(ToolDefinitionSnapshot tool, CancellationToken ct)
    {
        EnsureEmbeddingGenerator();

        await _gate.WaitAsync(ct);
        try
        {
            if (_snapshots.TryGetValue(tool.Name, out var existing) &&
                string.Equals(existing.DefinitionHash, tool.DefinitionHash, StringComparison.Ordinal) &&
                _embeddings.ContainsKey(tool.Name))
            {
                return;
            }
        }
        finally
        {
            _gate.Release();
        }

        var embedding = await GenerateEmbeddingAsync(tool.EmbeddingText, ct);

        await _gate.WaitAsync(ct);
        try
        {
            _snapshots[tool.Name] = tool;
            _embeddings[tool.Name] = embedding;
            BumpRevisionAndClearCache();
        }
        finally
        {
            _gate.Release();
        }
    }

    public bool RemoveTool(string toolName)
    {
        _gate.Wait();
        try
        {
            var removed = _snapshots.Remove(toolName);
            removed |= _embeddings.Remove(toolName);
            if (removed)
                BumpRevisionAndClearCache();
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ToolRouteCandidate>> SearchAsync(
        string prompt,
        IReadOnlyCollection<string> candidateToolNames,
        int topK,
        float minScore,
        string mode,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt) || candidateToolNames.Count == 0 || topK <= 0)
            return [];

        EnsureEmbeddingGenerator();

        var normalizedMode = ToolSemanticRoutingModes.Normalize(mode);
        var revision = Revision;
        var cacheKey = BuildCacheKey(prompt, candidateToolNames, topK, minScore, normalizedMode, revision);
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        var queryEmbedding = await GenerateEmbeddingAsync(prompt, ct);
        List<(ToolDefinitionSnapshot Snapshot, float[] Embedding)> localCandidates;

        await _gate.WaitAsync(ct);
        try
        {
            localCandidates = candidateToolNames
                .Where(_snapshots.ContainsKey)
                .Where(_embeddings.ContainsKey)
                .Select(name => (_snapshots[name], _embeddings[name]))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        var results = localCandidates
            .Select(candidate =>
            {
                var score = CosineSimilarity(queryEmbedding, candidate.Embedding);
                if (normalizedMode is ToolSemanticRoutingModes.Balanced or ToolSemanticRoutingModes.Accurate)
                    score += LexicalBoost(prompt, candidate.Snapshot);
                return new ToolRouteCandidate(candidate.Snapshot.Name, score);
            })
            .Where(candidate => candidate.Score >= minScore)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.ToolName, StringComparer.Ordinal)
            .Take(topK)
            .ToArray();

        Cache(cacheKey, results);
        return results;
    }

    public void ClearCache()
    {
        _gate.Wait();
        try
        {
            _queryCache.Clear();
            _queryCacheOrder.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct)
    {
        var generator = EnsureEmbeddingGenerator();
        var generated = await generator.GenerateAsync([text], cancellationToken: ct);
        if (generated.Count == 0)
            throw new InvalidOperationException("Embedding generator returned no vectors.");
        return generated[0].Vector.ToArray();
    }

    private IEmbeddingGenerator<string, Embedding<float>> EnsureEmbeddingGenerator()
        => _embeddingGenerator ?? throw new InvalidOperationException(
            "Tool semantic routing is enabled but no IEmbeddingGenerator<string, Embedding<float>> is registered.");

    private bool TryGetCached(string cacheKey, out IReadOnlyList<ToolRouteCandidate> result)
    {
        result = [];
        if (_config.QueryCacheSize <= 0)
            return false;

        _gate.Wait();
        try
        {
            if (!_queryCache.TryGetValue(cacheKey, out var cached))
                return false;

            result = cached;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Cache(string cacheKey, IReadOnlyList<ToolRouteCandidate> result)
    {
        if (_config.QueryCacheSize <= 0)
            return;

        _gate.Wait();
        try
        {
            if (!_queryCache.ContainsKey(cacheKey))
                _queryCacheOrder.Enqueue(cacheKey);

            _queryCache[cacheKey] = result;
            while (_queryCache.Count > _config.QueryCacheSize && _queryCacheOrder.TryDequeue(out var oldest))
                _queryCache.Remove(oldest);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void BumpRevisionAndClearCache()
    {
        _revision++;
        _queryCache.Clear();
        _queryCacheOrder.Clear();
        _logger?.LogDebug("Tool semantic index revision advanced to {Revision}.", _revision);
    }

    private static string BuildCacheKey(
        string prompt,
        IReadOnlyCollection<string> candidateToolNames,
        int topK,
        float minScore,
        string mode,
        long revision)
        => string.Join(
            '\u001f',
            revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
            mode,
            topK.ToString(System.Globalization.CultureInfo.InvariantCulture),
            minScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
            prompt,
            string.Join('\u001e', candidateToolNames.OrderBy(static name => name, StringComparer.Ordinal)));

    internal static float CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 0f;

        float dot = 0f;
        float normA = 0f;
        float normB = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0f ? 0f : dot / denominator;
    }

    private static float LexicalBoost(string prompt, ToolDefinitionSnapshot tool)
    {
        var normalizedPrompt = prompt.Trim();
        if (normalizedPrompt.Length == 0)
            return 0f;

        var score = 0f;
        if (string.Equals(normalizedPrompt, tool.Name, StringComparison.OrdinalIgnoreCase))
            score += 0.25f;
        else if (normalizedPrompt.Contains(tool.Name, StringComparison.OrdinalIgnoreCase))
            score += 0.15f;

        foreach (var token in SplitTokens(normalizedPrompt))
        {
            if (string.Equals(token, tool.Name, StringComparison.OrdinalIgnoreCase))
                score += 0.12f;
            else if (tool.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 0.05f;
            else if (tool.Description.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 0.03f;
            else if (tool.ParameterSchema.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 0.015f;
        }

        return Math.Min(score, 0.35f);
    }

    private static IEnumerable<string> SplitTokens(string value)
        => value.Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 2);
}
