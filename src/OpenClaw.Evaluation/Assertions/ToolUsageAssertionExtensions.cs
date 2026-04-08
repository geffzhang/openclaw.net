using OpenClaw.Evaluation.Models;

namespace OpenClaw.Evaluation.Assertions;

/// <summary>
/// Extension methods providing the fluent <c>.Should()</c> entry point for
/// tool usage assertions on <see cref="AgentExecutionTrace"/> and <see cref="EvaluationResult"/>.
/// </summary>
public static class ToolUsageAssertionExtensions
{
    /// <summary>
    /// Begins a fluent assertion chain on the tool invocations within an execution trace.
    /// Call <see cref="ToolUsageAssertions.Validate"/> (or let the chain auto-validate) to finalize.
    /// </summary>
    public static ToolUsageAssertions Should(this AgentExecutionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return new ToolUsageAssertions(trace.ToolInvocations);
    }

    /// <summary>
    /// Begins a fluent assertion chain on the tool invocations within an evaluation result.
    /// </summary>
    public static ToolUsageAssertions Should(this EvaluationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Trace is null)
            throw new InvalidOperationException("EvaluationResult.Trace must be set before calling Should().");
        return new ToolUsageAssertions(result.Trace.ToolInvocations);
    }
}
