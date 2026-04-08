namespace OpenClaw.Evaluation.Models;

/// <summary>
/// Aggregated execution trace for a single agent session, constructed from
/// intercepted <see cref="System.Diagnostics.Activity"/> traces emitted by
/// the OpenClaw runtime.
/// </summary>
public sealed class AgentExecutionTrace
{
    /// <summary>The session identifier that scopes this trace.</summary>
    public string SessionId { get; }

    /// <summary>Ordered list of tool invocations captured during the session.</summary>
    public IReadOnlyList<ToolCallRecord> ToolInvocations { get; }

    /// <summary>Total number of LLM calls observed in the trace.</summary>
    public int LlmCallCount { get; init; }

    /// <summary>Total input tokens consumed across all LLM calls in the trace.</summary>
    public long TotalInputTokens { get; init; }

    /// <summary>Total output tokens produced across all LLM calls in the trace.</summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>Overall wall-clock duration of the traced session segment.</summary>
    public TimeSpan TotalDuration { get; init; }

    public AgentExecutionTrace(string sessionId, IReadOnlyList<ToolCallRecord> toolInvocations)
    {
        SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        ToolInvocations = toolInvocations ?? throw new ArgumentNullException(nameof(toolInvocations));
    }

    /// <summary>Returns true when every tool invocation in the trace succeeded.</summary>
    public bool AllToolsSucceeded => ToolInvocations.All(t => t.IsSuccessful);

    /// <summary>Returns the count of failed tool invocations.</summary>
    public int FailedToolCount => ToolInvocations.Count(t => !t.IsSuccessful);

    /// <summary>Returns the count of timed-out tool invocations.</summary>
    public int TimedOutToolCount => ToolInvocations.Count(t => t.IsTimedOut);
}
