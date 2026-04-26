using System.Text.Json;

namespace OpenClaw.Core.Models;

/// <summary>
/// A2UI protocol message sent from the gateway to a declarative UI client.
/// </summary>
public sealed record A2UIInstruction
{
    public required string Type { get; init; }
    public string? SurfaceId { get; init; }
    public string? Path { get; init; }
    public JsonElement? Value { get; init; }
    public JsonElement? Components { get; init; }
    public string? MessageId { get; init; }
}

/// <summary>
/// A2UI client interaction event sent from the declarative UI client to OpenClaw.
/// </summary>
public sealed record A2UIClientEvent
{
    public required string Type { get; init; }
    public string? SurfaceId { get; init; }
    public string? ActionId { get; init; }
    public string? ComponentId { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public JsonElement? Data { get; init; }
}

public static class A2UIProtocol
{
    public const int MaxPointerLength = 512;
    public const int MaxComponentTypeLength = 128;

    public static bool IsSupportedInstructionType(string? type)
        => string.Equals(type, "createSurface", StringComparison.Ordinal) ||
           string.Equals(type, "updateDataModel", StringComparison.Ordinal) ||
           string.Equals(type, "updateComponents", StringComparison.Ordinal);

    public static bool IsSafeJsonPointer(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPointerLength)
            return false;

        if (path[0] != '/')
            return false;

        for (var i = 0; i < path.Length; i++)
        {
            var ch = path[i];
            if (char.IsControl(ch))
                return false;

            if (ch == '~')
            {
                if (i + 1 >= path.Length)
                    return false;

                var next = path[i + 1];
                if (next is not ('0' or '1'))
                    return false;

                i++;
            }
        }

        return true;
    }

    public static bool ContainsOnlyAllowedComponents(JsonElement components, IReadOnlySet<string> allowedComponentTypes)
    {
        if (allowedComponentTypes.Count == 0)
            return true;

        return WalkComponents(components, allowedComponentTypes);
    }

    private static bool WalkComponents(JsonElement element, IReadOnlySet<string> allowedComponentTypes)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String)
                {
                    var type = typeElement.GetString();
                    if (string.IsNullOrWhiteSpace(type) ||
                        type.Length > MaxComponentTypeLength ||
                        !allowedComponentTypes.Contains(type))
                    {
                        return false;
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (!WalkComponents(property.Value, allowedComponentTypes))
                        return false;
                }

                return true;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!WalkComponents(item, allowedComponentTypes))
                        return false;
                }

                return true;

            default:
                return true;
        }
    }
}
