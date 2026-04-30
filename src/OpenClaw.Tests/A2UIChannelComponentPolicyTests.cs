using System.Net;
using System.Text;
using System.Text.Json;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for the dual-path unification behavior change introduced in step 2 of the A2UI plan:
/// an empty <see cref="A2UIChannelConfig.AllowedComponentTypes"/> now falls back to the unified
/// default 11-type dictionary instead of admitting any component type.
/// </summary>
public sealed class A2UIChannelComponentPolicyTests
{
    [Fact]
    public async Task DefaultConfig_AcceptsCanonicalComponentType()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("c1", ws, IPAddress.Loopback));

        using var components = JsonDocument.Parse("""{"type":"text","id":"a","text":"hi"}""");
        await channel.SendInstructionAsync(
            "c1",
            new A2UIInstruction
            {
                Type = "updateComponents",
                Components = components.RootElement.Clone()
            },
            CancellationToken.None);

        var payload = Encoding.UTF8.GetString(ws.Sent.Single());
        var parsed = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.A2UIInstruction);
        Assert.Equal("updateComponents", parsed!.Type);
    }

    [Fact]
    public async Task DefaultConfig_RejectsUnknownComponentType()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("c1", ws, IPAddress.Loopback));

        using var components = JsonDocument.Parse("""{"type":"evil-script","id":"a"}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendInstructionAsync(
                "c1",
                new A2UIInstruction
                {
                    Type = "updateComponents",
                    Components = components.RootElement.Clone()
                },
                CancellationToken.None).AsTask());

        Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AllowAnyComponentType_TruePermitsArbitraryTypes()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig { AllowAnyComponentType = true });
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("c1", ws, IPAddress.Loopback));

        using var components = JsonDocument.Parse("""{"type":"any-custom-thing","id":"a"}""");
        await channel.SendInstructionAsync(
            "c1",
            new A2UIInstruction
            {
                Type = "updateComponents",
                Components = components.RootElement.Clone()
            },
            CancellationToken.None);

        Assert.Single(ws.Sent);
    }

    [Fact]
    public async Task ExplicitAllowedTypes_OverridesDefaultDictionary()
    {
        // Explicit allow-list of ["text"] only — "button" is in the default dictionary but must be
        // rejected because the explicit list overrides it.
        var channel = new A2UIChannel(new A2UIChannelConfig { AllowedComponentTypes = ["text"] });
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("c1", ws, IPAddress.Loopback));

        using var components = JsonDocument.Parse("""{"type":"button","id":"a","label":"OK"}""");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendInstructionAsync(
                "c1",
                new A2UIInstruction
                {
                    Type = "updateComponents",
                    Components = components.RootElement.Clone()
                },
                CancellationToken.None).AsTask());

        Assert.Contains("not allowed", ex.Message, StringComparison.Ordinal);
    }
}
