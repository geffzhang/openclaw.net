using System.Collections.Concurrent;
using System.Diagnostics;

namespace OpenClaw.Evaluation.Telemetry;

/// <summary>
/// Thread-safe in-memory collector that subscribes to <see cref="ActivitySource"/> events
/// emitted by the OpenClaw runtime. Captured activities can later be transformed into
/// <see cref="Models.AgentExecutionTrace"/> objects via <see cref="ActivityTraceExtractor"/>.
/// </summary>
public sealed class InMemoryTraceCollector : IDisposable
{
    private readonly ConcurrentBag<Activity> _activities = new();
    private readonly ActivityListener _listener;

    /// <summary>
    /// Creates a collector that listens on the specified activity source names.
    /// When no source names are provided, defaults to "OpenClaw.Gateway".
    /// </summary>
    public InMemoryTraceCollector(params string[] sourceNames)
    {
        var names = sourceNames.Length > 0
            ? new HashSet<string>(sourceNames, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal) { Core.Observability.Telemetry.ServiceName };

        _listener = new ActivityListener
        {
            ShouldListenTo = source => names.Contains(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };

        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Returns a snapshot of all captured activities.</summary>
    public IReadOnlyList<Activity> GetActivities() => _activities.ToArray();

    /// <summary>Clears all captured activities.</summary>
    public void Clear()
    {
        // ConcurrentBag doesn't have a Clear method; drain it
        while (_activities.TryTake(out _)) { }
    }

    public void Dispose() => _listener.Dispose();
}
