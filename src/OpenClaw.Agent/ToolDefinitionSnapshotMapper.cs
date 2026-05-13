using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent;

internal static class ToolDefinitionSnapshotMapper
{
    public static ToolDefinitionSnapshot Create(ITool tool, string toolTextMode)
    {
        var normalizedMode = ToolSemanticRoutingToolTextModes.Normalize(toolTextMode);
        var schemaSummary = normalizedMode switch
        {
            ToolSemanticRoutingToolTextModes.FullSchema => tool.ParameterSchema,
            ToolSemanticRoutingToolTextModes.SchemaSummary => SummarizeSchema(tool.ParameterSchema),
            _ => string.Empty
        };

        var embeddingText = BuildEmbeddingText(tool.Name, tool.Description, schemaSummary);
        var hash = ComputeHash(tool.Name, tool.Description, tool.ParameterSchema);
        return new ToolDefinitionSnapshot(tool.Name, tool.Description, tool.ParameterSchema, embeddingText, hash);
    }

    private static string BuildEmbeddingText(string name, string description, string schemaSummary)
    {
        var sb = new StringBuilder();
        sb.Append("Tool name: ");
        sb.AppendLine(name);
        sb.Append("Tool name: ");
        sb.AppendLine(name);
        sb.Append("Description: ");
        sb.AppendLine(description);

        if (!string.IsNullOrWhiteSpace(schemaSummary))
        {
            sb.Append("Parameters: ");
            sb.AppendLine(schemaSummary);
        }

        return sb.ToString();
    }

    private static string SummarizeSchema(string parameterSchema)
    {
        if (string.IsNullOrWhiteSpace(parameterSchema))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(parameterSchema);
            var root = document.RootElement;
            var required = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("required", out var requiredElement) &&
                requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in requiredElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        required.Add(item.GetString()!);
                }
            }

            if (!root.TryGetProperty("properties", out var properties) ||
                properties.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var property in properties.EnumerateObject())
            {
                var segment = property.Name;
                if (required.Contains(property.Name))
                    segment += " required";

                if (property.Value.TryGetProperty("description", out var description) &&
                    description.ValueKind == JsonValueKind.String)
                {
                    var text = description.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        segment += $" - {Truncate(text.Trim(), 160)}";
                }

                parts.Add(segment);
            }

            return string.Join("; ", parts);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string ComputeHash(string name, string description, string schema)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{name}\n{description}\n{schema}"));
        return Convert.ToHexString(bytes);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
