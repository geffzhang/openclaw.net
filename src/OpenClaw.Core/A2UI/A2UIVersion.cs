namespace OpenClaw.Core.A2UI;

/// <summary>
/// A2UI protocol versions supported by the unified subsystem. Used by <see cref="A2UISession"/> to
/// route instructions to the right wire format.
/// </summary>
/// <remarks>
/// <para>
/// <b>v0.8</b> — JSON Lines frames embedded in the Canvas (`/ws`) sub-protocol. Production-grade,
/// consumed by webchat and Companion today.
/// </para>
/// <para>
/// <b>v0.9</b> — Standalone instruction channel (`/a2ui/stream`) with `createSurface` /
/// `updateDataModel` / `updateComponents` commands. Optional, default-off, used by the InsForge
/// realtime bridge.
/// </para>
/// </remarks>
public enum A2UIVersion
{
    /// <summary>Unknown / not yet negotiated.</summary>
    Unknown = 0,

    /// <summary>v0.8 JSONL frame contract.</summary>
    V0_8 = 1,

    /// <summary>v0.9 instruction contract.</summary>
    V0_9 = 2,
}
