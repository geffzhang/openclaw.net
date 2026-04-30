using System.Text.Json;

namespace OpenClaw.Core.A2UI;

/// <summary>
/// Single source of truth for which A2UI component types are accepted on the wire.
/// </summary>
/// <remarks>
/// <para>
/// The same dictionary covers v0.8 JSONL frames (one component per line) and v0.9
/// <c>updateComponents</c> trees (a nested dictionary of components). Treating the two as one
/// schema is the cornerstone of the dual-path unification plan: a single allow-list, a single
/// validator, a single set of tests.
/// </para>
/// <para>
/// Construction policy:
/// <list type="bullet">
///   <item>
///     Empty user-provided allow-list ⇒ use <see cref="DefaultComponentTypes"/> (the 11 production
///     types). This is a deliberate hardening of the previous "empty = allow anything" default.
///   </item>
///   <item>
///     Non-empty user-provided allow-list ⇒ use exactly that list (case-sensitive, matches
///     <see cref="StringComparer.Ordinal"/>).
///   </item>
///   <item>
///     <c>allowAny = true</c> ⇒ disables the allow-list entirely. Reserved for the legacy
///     "allow all" behavior and InsForge passthrough scenarios.
///   </item>
/// </list>
/// </para>
/// </remarks>
public sealed class ComponentTypePolicy
{
    /// <summary>
    /// The 11 canonical v0.8 / v0.9 component types. Keep in sync with the v0.8 frame validator
    /// and the rendering side of webchat / Companion.
    /// </summary>
    public static IReadOnlyList<string> DefaultComponentTypes { get; } = new[]
    {
        "text",
        "markdown",
        "card",
        "button",
        "input",
        "select",
        "checklist",
        "table",
        "image",
        "progress",
        "chart",
    };

    private static readonly HashSet<string> DefaultSet = new(DefaultComponentTypes, StringComparer.Ordinal);

    private readonly HashSet<string> _allowed;
    private readonly bool _allowAny;

    private ComponentTypePolicy(HashSet<string> allowed, bool allowAny)
    {
        _allowed = allowed;
        _allowAny = allowAny;
    }

    /// <summary>
    /// A policy that allows any component type. Use only for trusted-passthrough scenarios.
    /// </summary>
    public static ComponentTypePolicy AllowAny { get; } = new(new HashSet<string>(StringComparer.Ordinal), allowAny: true);

    /// <summary>The default policy: the 11 canonical types.</summary>
    public static ComponentTypePolicy Default { get; } = new(new HashSet<string>(DefaultSet, StringComparer.Ordinal), allowAny: false);

    /// <summary>
    /// Builds a policy from configuration semantics: empty list ⇒ default dictionary, non-empty ⇒
    /// explicit override, <paramref name="allowAny"/> wins over both.
    /// </summary>
    public static ComponentTypePolicy FromConfig(IEnumerable<string>? allowedComponentTypes, bool allowAny)
    {
        if (allowAny)
            return AllowAny;

        if (allowedComponentTypes is null)
            return Default;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in allowedComponentTypes)
        {
            if (!string.IsNullOrWhiteSpace(type))
                set.Add(type);
        }

        return set.Count == 0 ? Default : new ComponentTypePolicy(set, allowAny: false);
    }

    /// <summary>True when this policy permits every component type without inspection.</summary>
    public bool AllowsAny => _allowAny;

    /// <summary>The configured set of allowed types (empty when <see cref="AllowsAny"/> is true).</summary>
    public IReadOnlyCollection<string> AllowedTypes => _allowed;

    /// <summary>True when the supplied component type is admitted by this policy.</summary>
    public bool IsAllowed(string? type)
    {
        if (_allowAny)
            return true;
        if (string.IsNullOrWhiteSpace(type) || type.Length > A2UIProtocolLimits.MaxComponentTypeLength)
            return false;
        return _allowed.Contains(type);
    }

    /// <summary>
    /// Recursively validates that every <c>type</c> property in a v0.9 components tree is allowed.
    /// </summary>
    public bool Accepts(JsonElement components)
    {
        if (_allowAny)
            return true;

        return Walk(components);
    }

    private bool Walk(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind == JsonValueKind.String &&
                    !IsAllowed(typeElement.GetString()))
                {
                    return false;
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (!Walk(property.Value))
                        return false;
                }

                return true;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!Walk(item))
                        return false;
                }

                return true;

            default:
                return true;
        }
    }
}

/// <summary>
/// Numeric limits shared across the A2UI protocol layer. Kept on a separate type so that
/// <see cref="ComponentTypePolicy"/> can reference them without depending on
/// <c>OpenClaw.Core.Models.A2UIProtocol</c> (which itself uses these values).
/// </summary>
public static class A2UIProtocolLimits
{
    /// <summary>Maximum byte length of a JSON Pointer path accepted by the protocol.</summary>
    public const int MaxPointerLength = 512;

    /// <summary>Maximum length of a component <c>type</c> string.</summary>
    public const int MaxComponentTypeLength = 128;
}
