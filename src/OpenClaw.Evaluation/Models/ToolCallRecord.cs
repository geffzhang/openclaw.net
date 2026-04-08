namespace OpenClaw.Evaluation.Models;

/// <summary>
/// Immutable record capturing a single tool invocation within an agent execution trace.
/// Designed for NativeAOT: no reflection, no dynamic dispatch.
/// </summary>
public sealed record ToolCallRecord
{
    /// <summary>Name of the tool that was invoked (e.g. "web_search", "file_read").</summary>
    public required string ToolName { get; init; }

    /// <summary>Wall-clock duration of the tool execution in milliseconds.</summary>
    public double DurationMilliseconds { get; init; }

    /// <summary>Whether the tool execution completed successfully.</summary>
    public bool IsSuccessful { get; init; }

    /// <summary>Whether the tool execution timed out.</summary>
    public bool IsTimedOut { get; init; }

    /// <summary>Number of input tokens consumed during this tool call, if tracked.</summary>
    public long InputTokens { get; init; }

    /// <summary>Number of output tokens produced during this tool call, if tracked.</summary>
    public long OutputTokens { get; init; }

    /// <summary>UTC timestamp when the tool invocation started.</summary>
    public DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Optional error message when <see cref="IsSuccessful"/> is false.</summary>
    public string? ErrorMessage { get; init; }
}
