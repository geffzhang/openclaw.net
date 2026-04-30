namespace OpenClaw.Core.A2UI;

/// <summary>
/// Capability strings advertised by A2UI clients during the connection handshake
/// (`canvas_ready.capabilities` for `/ws` Canvas, or the `hello` frame on `/a2ui/stream`).
/// </summary>
/// <remarks>
/// Capabilities are case-insensitive on the wire but exposed here in canonical lower-snake form.
/// Servers MUST tolerate unknown capabilities and clients MUST tolerate version-specific server
/// behavior gated on advertised capabilities.
/// </remarks>
public static class A2UICapabilities
{
    /// <summary>Client speaks the v0.8 JSONL frame contract.</summary>
    public const string V0_8 = "a2ui.v0_8";

    /// <summary>Client speaks the v0.9 instruction contract.</summary>
    public const string V0_9 = "a2ui.v0_9";

    /// <summary>Client supports the `eval` JS-call extension.</summary>
    public const string Eval = "a2ui.eval";

    /// <summary>Client supports `updateDataModel` (RFC 6901 JSON Pointer).</summary>
    public const string DataModel = "a2ui.data_model";

    /// <summary>Client supports `updateComponents` component-tree merges.</summary>
    public const string Components = "a2ui.components";

    /// <summary>Client supports `createSurface` for multi-surface scenarios.</summary>
    public const string CreateSurface = "a2ui.create_surface";

    /// <summary>
    /// Returns the highest A2UI version implied by the supplied capability list, or
    /// <see cref="A2UIVersion.Unknown"/> if no version capability is present.
    /// </summary>
    public static A2UIVersion ResolveVersion(IEnumerable<string>? capabilities)
    {
        if (capabilities is null)
            return A2UIVersion.Unknown;

        var hasV09 = false;
        var hasV08 = false;
        foreach (var raw in capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            if (string.Equals(trimmed, V0_9, StringComparison.OrdinalIgnoreCase))
                hasV09 = true;
            else if (string.Equals(trimmed, V0_8, StringComparison.OrdinalIgnoreCase))
                hasV08 = true;
        }

        if (hasV09)
            return A2UIVersion.V0_9;
        if (hasV08)
            return A2UIVersion.V0_8;

        return A2UIVersion.Unknown;
    }

    /// <summary>
    /// Returns true when the capability list contains <paramref name="capability"/> using
    /// case-insensitive matching, ignoring null / whitespace entries.
    /// </summary>
    public static bool Contains(IEnumerable<string>? capabilities, string capability)
    {
        if (capabilities is null || string.IsNullOrWhiteSpace(capability))
            return false;

        foreach (var raw in capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (string.Equals(raw.Trim(), capability, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
