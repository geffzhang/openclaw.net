using System.Net;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Plan step 8 — backfill rate-limit / per-IP / connection-cap / receive-timeout coverage that
/// previously had no direct tests. These exercise <see cref="A2UIChannel"/> via the existing
/// <c>TryAddConnectionForTest</c> hook so they remain hermetic (no real WebSocket server).
/// </summary>
public sealed class A2UIChannelLimitsTests
{
    [Fact]
    public void TryAddConnection_RejectsWhenGlobalConnectionCapReached()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig
        {
            MaxConnections = 2,
            MaxConnectionsPerIp = 16
        });

        Assert.True(channel.TryAddConnectionForTest("c1", new TestWebSocket(), IPAddress.Parse("10.0.0.1")));
        Assert.True(channel.TryAddConnectionForTest("c2", new TestWebSocket(), IPAddress.Parse("10.0.0.2")));
        Assert.False(channel.TryAddConnectionForTest("c3", new TestWebSocket(), IPAddress.Parse("10.0.0.3")));
    }

    [Fact]
    public void TryAddConnection_RejectsWhenPerIpCapReached()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig
        {
            MaxConnections = 100,
            MaxConnectionsPerIp = 2
        });

        var ip = IPAddress.Parse("203.0.113.1");
        Assert.True(channel.TryAddConnectionForTest("c1", new TestWebSocket(), ip));
        Assert.True(channel.TryAddConnectionForTest("c2", new TestWebSocket(), ip));
        // Third connection from the same IP must be rejected.
        Assert.False(channel.TryAddConnectionForTest("c3", new TestWebSocket(), ip));
        // But a connection from a different IP still succeeds.
        Assert.True(channel.TryAddConnectionForTest("c4", new TestWebSocket(), IPAddress.Parse("198.51.100.1")));
    }

    [Fact]
    public void TryAddConnection_TreatsNullRemoteIpAsSinglePerIpBucket()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig
        {
            MaxConnectionsPerIp = 1
        });

        Assert.True(channel.TryAddConnectionForTest("c1", new TestWebSocket(), remoteIp: null));
        // Second null-IP connection shares the "unknown" bucket and is therefore rejected.
        Assert.False(channel.TryAddConnectionForTest("c2", new TestWebSocket(), remoteIp: null));
    }

    [Fact]
    public void TryAddConnection_RejectsDuplicateClientId()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        Assert.True(channel.TryAddConnectionForTest("dup", new TestWebSocket(), IPAddress.Loopback));
        Assert.False(channel.TryAddConnectionForTest("dup", new TestWebSocket(), IPAddress.Loopback));
    }

    [Fact]
    public async Task SendInstructionAsync_NoOpWhenRecipientUnknown()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());

        // No connection registered for "ghost"; should silently no-op rather than throw.
        await channel.SendInstructionAsync(
            "ghost",
            new A2UIInstruction { Type = "updateDataModel", Path = "/x", Value = null },
            CancellationToken.None);
    }

    [Fact]
    public async Task SendInstructionAsync_RejectsUnsupportedInstructionType()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        Assert.True(channel.TryAddConnectionForTest("c1", new TestWebSocket(), IPAddress.Loopback));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendInstructionAsync(
                "c1",
                new A2UIInstruction { Type = "doSomethingWeird" },
                CancellationToken.None).AsTask());

        Assert.Contains("Unsupported", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendInstructionAsync_RejectsUnsafeJsonPointer()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        Assert.True(channel.TryAddConnectionForTest("c1", new TestWebSocket(), IPAddress.Loopback));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            channel.SendInstructionAsync(
                "c1",
                new A2UIInstruction { Type = "updateDataModel", Path = "no-leading-slash" },
                CancellationToken.None).AsTask());

        Assert.Contains("JSON Pointer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
