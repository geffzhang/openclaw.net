using OpenClaw.Core.A2UI;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2UISessionTests
{
    [Fact]
    public void Canvas_DefaultsToV0_8WhenNoCapabilitiesAdvertised()
    {
        var session = A2UISession.ForCanvas("client-1");

        Assert.Equal(A2UIVersion.V0_8, session.Version);
        Assert.Equal(A2UISessionOrigin.Canvas, session.Origin);
        Assert.True(session.SupportsV08);
        Assert.False(session.SupportsV09);
    }

    [Fact]
    public void Standalone_DefaultsToV0_9WhenNoCapabilitiesAdvertised()
    {
        var session = A2UISession.ForStandalone("client-1");

        Assert.Equal(A2UIVersion.V0_9, session.Version);
        Assert.Equal(A2UISessionOrigin.Standalone, session.Origin);
        Assert.True(session.SupportsV09);
        // Standalone sessions are not Canvas-compatible by default.
        Assert.False(session.SupportsV08);
    }

    [Fact]
    public void V0_9CapabilityWinsOverV0_8WhenBothAdvertised()
    {
        var session = A2UISession.ForCanvas("client-1", new[] { A2UICapabilities.V0_8, A2UICapabilities.V0_9 });

        Assert.Equal(A2UIVersion.V0_9, session.Version);
        Assert.True(session.SupportsV09);
        // Canvas origin always retains v0.8 fallback for back-compat.
        Assert.True(session.SupportsV08);
    }

    [Fact]
    public void HasCapability_IsCaseInsensitiveAndIgnoresWhitespace()
    {
        var session = A2UISession.ForStandalone("client-1", new[] { "  A2UI.Eval " });

        Assert.True(session.HasCapability(A2UICapabilities.Eval));
        Assert.False(session.HasCapability(A2UICapabilities.Components));
    }

    [Fact]
    public void UpdateCapabilities_ReplacesAndReResolvesVersion()
    {
        var session = A2UISession.ForCanvas("client-1");
        Assert.Equal(A2UIVersion.V0_8, session.Version);

        session.UpdateCapabilities(new[] { A2UICapabilities.V0_9, A2UICapabilities.Components });

        Assert.Equal(A2UIVersion.V0_9, session.Version);
        Assert.True(session.HasCapability(A2UICapabilities.Components));
    }

    [Fact]
    public void NextSequence_IsMonotonicAndStartsAtOne()
    {
        var session = A2UISession.ForStandalone("client-1");

        Assert.Equal(1L, session.NextSequence());
        Assert.Equal(2L, session.NextSequence());
        Assert.Equal(3L, session.NextSequence());
    }

    [Fact]
    public void RegisterSurface_TracksSurfaceIds()
    {
        var session = A2UISession.ForStandalone("client-1");
        session.RegisterSurface("main");
        session.RegisterSurface("dashboard");
        session.RegisterSurface("");
        session.RegisterSurface("main"); // duplicate

        Assert.Equal(2, session.Surfaces.Count);
        Assert.Contains("main", session.Surfaces);
        Assert.Contains("dashboard", session.Surfaces);
    }

    [Fact]
    public void ResolveVersion_ReturnsUnknownForEmptyOrUnrelatedCapabilities()
    {
        Assert.Equal(A2UIVersion.Unknown, A2UICapabilities.ResolveVersion(null));
        Assert.Equal(A2UIVersion.Unknown, A2UICapabilities.ResolveVersion(Array.Empty<string>()));
        Assert.Equal(A2UIVersion.Unknown, A2UICapabilities.ResolveVersion(new[] { "canvas.snapshot", "" }));
    }

    [Fact]
    public void Capabilities_NormalizationDeduplicatesEntries()
    {
        var session = A2UISession.ForStandalone(
            "client-1",
            new string?[] { A2UICapabilities.V0_9, A2UICapabilities.V0_9, " ", null, A2UICapabilities.Eval }!);

        Assert.Equal(2, session.Capabilities.Count);
    }
}
