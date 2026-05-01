using OpenClaw.Core.Models;

namespace OpenClaw.Core.A2UI;

/// <summary>
/// Plan step 6 — bridges the unified <c>OpenClaw:A2UI</c> section onto the legacy
/// <c>OpenClaw:Canvas</c>, <c>OpenClaw:Channels:A2UI</c>, and <c>OpenClaw:InsForge</c> keys.
///
/// Strategy: legacy keys remain authoritative for at least one release. When a field on the
/// unified section is set (non-null), it overrides the corresponding legacy field. When the
/// unified field is unset, the legacy field is used unchanged.
///
/// The helper records which legacy fields were the only source for a value, so callers can
/// surface a single deprecation warning at startup.
/// </summary>
public static class A2UIConfigMigration
{
    /// <summary>
    /// Apply the unified <see cref="A2UIConfig"/> overlay onto <paramref name="config"/>.
    /// Returns the list of legacy keys that are still being used (no unified equivalent set);
    /// callers should log a deprecation warning for each.
    /// </summary>
    public static IReadOnlyList<string> ApplyOverlay(GatewayConfig config)
    {
        var legacyOnly = new List<string>();
        var unified = config.A2UI;

        // ── Enabled ───────────────────────────────────────────────
        if (unified.Enabled is { } enabled)
        {
            config.Canvas.Enabled = enabled;
            config.Channels.A2UI.Enabled = enabled;
        }
        else
        {
            // Each check fires only when the legacy field deviates from its default
            // (Canvas.Enabled defaults to true, Channels.A2UI.Enabled defaults to false),
            // so a warning is recorded only when the user has explicitly set the legacy key.
            if (!config.Canvas.Enabled)
                legacyOnly.Add("OpenClaw:Canvas:Enabled");
            if (config.Channels.A2UI.Enabled)
                legacyOnly.Add("OpenClaw:Channels:A2UI:Enabled");
        }

        // ── Public-bind gate ──────────────────────────────────────
        if (unified.AllowOnPublicBind is { } allow)
        {
            config.Canvas.AllowOnPublicBind = allow;
        }
        else if (config.Canvas.AllowOnPublicBind)
        {
            legacyOnly.Add("OpenClaw:Canvas:AllowOnPublicBind");
        }

        // ── Connection (replaces OpenClaw:Channels:A2UI fields) ───
        var conn = unified.Connection;
        var legacyConn = config.Channels.A2UI;
        if (conn.MaxConnections is { } maxConn)
            legacyConn.MaxConnections = maxConn;
        if (conn.MaxConnectionsPerIp is { } maxPerIp)
            legacyConn.MaxConnectionsPerIp = maxPerIp;
        if (conn.MessagesPerMinutePerConnection is { } rate)
            legacyConn.MessagesPerMinutePerConnection = rate;
        if (conn.ReceiveTimeoutSeconds is { } timeout)
            legacyConn.ReceiveTimeoutSeconds = timeout;
        if (conn.MaxMessageBytes is { } maxMsg)
            legacyConn.MaxMessageBytes = maxMsg;

        // ── Frames ────────────────────────────────────────────────
        if (unified.Frames.MaxFramesPerPush is { } maxFrames)
            config.Canvas.MaxFramesPerPush = maxFrames;
        if (unified.Frames.MaxBytes is { } maxBytes)
            config.Canvas.MaxCommandBytes = maxBytes;
        if (unified.Frames.MaxInstructionBytes is { } maxInstr)
            config.Channels.A2UI.MaxInstructionBytes = maxInstr;

        // ── Components ────────────────────────────────────────────
        if (unified.Components.AllowedTypes is { } allowedTypes)
            config.Channels.A2UI.AllowedComponentTypes = allowedTypes;
        if (unified.Components.AllowAny is { } allowAny)
            config.Channels.A2UI.AllowAnyComponentType = allowAny;

        // ── InsForge (full block override) ────────────────────────
        if (unified.InsForge is { } insforge)
            config.InsForge = insforge;
        else if (config.InsForge.Enabled)
            legacyOnly.Add("OpenClaw:InsForge");

        return legacyOnly;
    }
}
