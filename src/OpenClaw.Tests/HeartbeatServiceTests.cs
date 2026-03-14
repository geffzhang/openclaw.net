using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class HeartbeatServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenDisabled_ReturnsImmediately()
    {
        var config = new GatewayConfig
        {
            Heartbeat = new HeartbeatConfig { Enabled = false }
        };
        var service = new HeartbeatService(config, NullLogger<HeartbeatService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, service.TickCount);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnabled_TicksAtLeastOnce()
    {
        var config = new GatewayConfig
        {
            Heartbeat = new HeartbeatConfig { Enabled = true, IntervalSeconds = 1 }
        };
        var service = new HeartbeatService(config, NullLogger<HeartbeatService>.Instance);

        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);

        // Wait enough for at least one tick (interval min is 5, but our config says 1 which gets clamped to 5)
        // We use a 6-second wait to ensure at least one tick fires.
        await Task.Delay(TimeSpan.FromSeconds(6));
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.True(service.TickCount >= 1, $"Expected at least 1 tick, got {service.TickCount}");
    }

    [Theory]
    [InlineData(null, LogLevel.Debug)]
    [InlineData("", LogLevel.Debug)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("invalid", LogLevel.Debug)]
    public void ParseLogLevel_ReturnsExpectedLevel(string? input, LogLevel expected)
    {
        Assert.Equal(expected, HeartbeatService.ParseLogLevel(input));
    }

    [Fact]
    public void HeartbeatConfig_DefaultValues()
    {
        var cfg = new HeartbeatConfig();
        Assert.False(cfg.Enabled);
        Assert.Equal(30, cfg.IntervalSeconds);
        Assert.Equal("Debug", cfg.LogLevel);
    }

    [Fact]
    public void GatewayConfig_HasHeartbeatProperty()
    {
        var gw = new GatewayConfig();
        Assert.NotNull(gw.Heartbeat);
        Assert.False(gw.Heartbeat.Enabled);
    }
}
