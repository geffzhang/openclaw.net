using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

/// <summary>
/// Periodic heartbeat background service.
/// Reads configuration from OpenClaw:Heartbeat (documented in HEARTBEAT.md).
/// Uses an async loop with <see cref="PeriodicTimer"/> and a <see cref="SemaphoreSlim"/>
/// gate to prevent re-entrance / concurrent execution of heartbeat ticks.
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    private readonly GatewayConfig _config;
    private readonly ILogger<HeartbeatService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _tickCount;

    public HeartbeatService(GatewayConfig config, ILogger<HeartbeatService> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Number of heartbeat ticks completed so far.</summary>
    public long TickCount => Interlocked.Read(ref _tickCount);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var heartbeat = _config.Heartbeat;
        if (!heartbeat.Enabled)
        {
            _logger.LogInformation("Heartbeat service is disabled.");
            return;
        }

        var intervalSeconds = Math.Max(5, heartbeat.IntervalSeconds);
        var logLevel = ParseLogLevel(heartbeat.LogLevel);

        _logger.LogInformation(
            "Heartbeat service started. Interval={IntervalSeconds}s, LogLevel={LogLevel}.",
            intervalSeconds,
            logLevel);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!await _gate.WaitAsync(0, stoppingToken))
            {
                _logger.LogWarning("Heartbeat tick skipped because the previous tick is still running.");
                continue;
            }

            try
            {
                await OnHeartbeatTickAsync(logLevel, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Graceful shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Heartbeat tick failed.");
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private Task OnHeartbeatTickAsync(LogLevel level, CancellationToken ct)
    {
        var tick = Interlocked.Increment(ref _tickCount);
        _logger.Log(level, "Heartbeat tick #{Tick} at {UtcNow:O}.", tick, DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    internal static LogLevel ParseLogLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return LogLevel.Debug;

        return Enum.TryParse<LogLevel>(value, ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Debug;
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
