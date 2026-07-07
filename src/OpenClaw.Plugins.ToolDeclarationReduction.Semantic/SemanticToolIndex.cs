using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

internal sealed class SemanticToolIndex
{
    private readonly HashingTextVectorizer _vectorizer;
    private readonly Entry[] _entries;

    public string Fingerprint { get; }

    private SemanticToolIndex(string fingerprint, HashingTextVectorizer vectorizer, Entry[] entries)
    {
        Fingerprint = fingerprint;
        _vectorizer = vectorizer;
        _entries = entries;
    }

    public static SemanticToolIndex Build(IReadOnlyList<AITool> tools)
    {
        var vectorizer = new HashingTextVectorizer();
        var entries = tools
            .Select(tool =>
            {
                var text = BuildIndexText(tool);
                return new Entry(tool, text, vectorizer.Vectorize(text));
            })
            .ToArray();

        return new SemanticToolIndex(BuildFingerprint(entries), vectorizer, entries);
    }

    public IReadOnlyList<SemanticToolSearchResult> Search(string query, int topK, double minScore)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var queryVector = _vectorizer.Vectorize(query);
        return _entries
            .Select(entry => new SemanticToolSearchResult(entry.Tool, CosineSimilarity.Score(queryVector, entry.Vector)))
            .Where(result => result.Score >= minScore)
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Tool.Name, StringComparer.Ordinal)
            .Take(topK)
            .ToArray();
    }

    private static string BuildIndexText(AITool tool)
    {
        var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
        var schema = declaration is null ? string.Empty : declaration.JsonSchema.ToString();
        return string.Concat(tool.Name, ": ", tool.Description, ". Parameters: ", schema);
    }

    private static string BuildFingerprint(IReadOnlyList<Entry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(static item => item.Tool.Name, StringComparer.Ordinal))
        {
            builder.Append(entry.Tool.Name);
            builder.Append('\0');
            builder.Append(entry.Text);
            builder.Append('\0');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private sealed record Entry(AITool Tool, string Text, float[] Vector);
}

internal sealed record SemanticToolSearchResult(AITool Tool, double Score);