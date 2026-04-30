namespace OpenClaw.Core.A2UI;

/// <summary>
/// Per-connection A2UI protocol state machine. Records the negotiated protocol version, the set of
/// surfaces the client has acknowledged, and a monotonic instruction sequence number.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="A2UISession"/> is the central abstraction introduced by the A2UI dual-path
/// unification: both the Canvas (`/ws`) entry and the standalone (`/a2ui/stream`) entry build a
/// session at connect time and use it to decide what wire format outbound updates take.
/// </para>
/// <para>
/// This type intentionally has no dependency on WebSocket primitives; the channel layer owns the
/// transport, this layer owns protocol-level state.
/// </para>
/// </remarks>
public sealed class A2UISession
{
    private readonly object _gate = new();
    private readonly HashSet<string> _surfaces = new(StringComparer.Ordinal);
    private long _sequence;

    private A2UISession(string clientId, A2UIVersion version, IReadOnlyCollection<string> capabilities, A2UISessionOrigin origin)
    {
        ClientId = clientId;
        Version = version;
        Capabilities = capabilities;
        Origin = origin;
    }

    /// <summary>Identifier of the underlying connection.</summary>
    public string ClientId { get; }

    /// <summary>Negotiated protocol version. May be <see cref="A2UIVersion.Unknown"/> when the
    /// client has not yet sent a handshake but the entry point has a known default.</summary>
    public A2UIVersion Version { get; private set; }

    /// <summary>Raw capability list as advertised by the client.</summary>
    public IReadOnlyCollection<string> Capabilities { get; private set; }

    /// <summary>Which entry point established this session.</summary>
    public A2UISessionOrigin Origin { get; }

    /// <summary>Currently-known surfaces created by the server for this session.</summary>
    public IReadOnlyCollection<string> Surfaces
    {
        get
        {
            lock (_gate)
                return _surfaces.ToArray();
        }
    }

    /// <summary>
    /// Creates a session for a `/ws` Canvas client. Defaults to <see cref="A2UIVersion.V0_8"/>
    /// when the client does not advertise an A2UI version capability — Canvas clients today speak
    /// v0.8 implicitly.
    /// </summary>
    public static A2UISession ForCanvas(string clientId, IEnumerable<string>? capabilities = null)
    {
        var caps = NormalizeCapabilities(capabilities);
        var version = A2UICapabilities.ResolveVersion(caps);
        if (version == A2UIVersion.Unknown)
            version = A2UIVersion.V0_8;
        return new A2UISession(clientId, version, caps, A2UISessionOrigin.Canvas);
    }

    /// <summary>
    /// Creates a session for a `/a2ui/stream` standalone client. Defaults to
    /// <see cref="A2UIVersion.V0_9"/> when the client does not advertise a version — clients on
    /// the standalone endpoint are presumed v0.9 native.
    /// </summary>
    public static A2UISession ForStandalone(string clientId, IEnumerable<string>? capabilities = null)
    {
        var caps = NormalizeCapabilities(capabilities);
        var version = A2UICapabilities.ResolveVersion(caps);
        if (version == A2UIVersion.Unknown)
            version = A2UIVersion.V0_9;
        return new A2UISession(clientId, version, caps, A2UISessionOrigin.Standalone);
    }

    /// <summary>
    /// Updates the session with capabilities received in a later handshake (e.g. a `canvas_ready`
    /// envelope arriving after connect). Re-resolves <see cref="Version"/>.
    /// </summary>
    public void UpdateCapabilities(IEnumerable<string>? capabilities)
    {
        var caps = NormalizeCapabilities(capabilities);
        var resolved = A2UICapabilities.ResolveVersion(caps);
        lock (_gate)
        {
            Capabilities = caps;
            if (resolved != A2UIVersion.Unknown)
                Version = resolved;
        }
    }

    /// <summary>True when the client advertised <paramref name="capability"/>.</summary>
    public bool HasCapability(string capability) => A2UICapabilities.Contains(Capabilities, capability);

    /// <summary>True when this session can receive v0.9 structured instructions
    /// (`updateDataModel` / `updateComponents` / `createSurface`).</summary>
    public bool SupportsV09 => Version == A2UIVersion.V0_9;

    /// <summary>
    /// True when this session can receive v0.8 JSONL frames.
    /// </summary>
    /// <remarks>
    /// Canvas-origin sessions retain v0.8 capability even after negotiating v0.9, because the
    /// Canvas (`/ws`) sub-protocol historically transports v0.8 frames and webchat / Companion
    /// renderers are first-party consumers of that wire format. Standalone (`/a2ui/stream`)
    /// sessions follow strict version routing.
    /// </remarks>
    public bool SupportsV08 => Version == A2UIVersion.V0_8 || Origin == A2UISessionOrigin.Canvas;

    /// <summary>Records that a surface was acknowledged by the server.</summary>
    public void RegisterSurface(string surfaceId)
    {
        if (string.IsNullOrWhiteSpace(surfaceId))
            return;
        lock (_gate)
            _surfaces.Add(surfaceId);
    }

    /// <summary>Returns the next monotonic sequence number for outbound instructions.</summary>
    public long NextSequence() => Interlocked.Increment(ref _sequence);

    private static IReadOnlyCollection<string> NormalizeCapabilities(IEnumerable<string>? capabilities)
    {
        if (capabilities is null)
            return Array.Empty<string>();

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in capabilities)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var trimmed = raw.Trim();
            if (seen.Add(trimmed))
                list.Add(trimmed);
        }

        return list;
    }
}

/// <summary>Which transport / entry point an <see cref="A2UISession"/> was established on.</summary>
public enum A2UISessionOrigin
{
    /// <summary>`/ws` Canvas sub-protocol; default version v0.8.</summary>
    Canvas = 0,

    /// <summary>`/a2ui/stream` standalone WebSocket; default version v0.9.</summary>
    Standalone = 1,
}
