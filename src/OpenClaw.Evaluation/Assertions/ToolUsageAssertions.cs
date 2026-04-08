using OpenClaw.Evaluation.Models;

namespace OpenClaw.Evaluation.Assertions;

/// <summary>
/// Fluent assertion API for verifying tool usage patterns in <see cref="AgentExecutionTrace"/>.
/// Integrates naturally with xUnit via standard exception-based assertion failures.
/// </summary>
public sealed class ToolUsageAssertions
{
    private readonly IReadOnlyList<ToolCallRecord> _invocations;
    private readonly List<string> _errors = new();

    internal ToolUsageAssertions(IReadOnlyList<ToolCallRecord> invocations)
    {
        _invocations = invocations ?? throw new ArgumentNullException(nameof(invocations));
    }

    /// <summary>
    /// Asserts that the specified tool was called at least once.
    /// Returns a <see cref="ToolCallChain"/> for further chained assertions on that tool.
    /// </summary>
    public ToolCallChain HaveCalledTool(string toolName)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        var found = _invocations.Any(t =>
            string.Equals(t.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

        if (!found)
            _errors.Add($"Expected tool '{toolName}' to have been called, but it was not found in the trace.");

        return new ToolCallChain(this, toolName);
    }

    /// <summary>
    /// Asserts that no tool invocations resulted in errors.
    /// </summary>
    public ToolUsageAssertions HaveNoErrors()
    {
        var failures = _invocations.Where(t => !t.IsSuccessful).ToList();
        if (failures.Count > 0)
        {
            var names = string.Join(", ", failures.Select(f => $"'{f.ToolName}'"));
            _errors.Add($"Expected no tool errors, but {failures.Count} tool(s) failed: {names}.");
        }
        return this;
    }

    /// <summary>
    /// Asserts that at least one tool invocation resulted in an error.
    /// Useful for red-team / security boundary tests where dangerous tools should fail.
    /// </summary>
    public ToolUsageAssertions HaveErrors()
    {
        var hasFailures = _invocations.Any(t => !t.IsSuccessful);
        if (!hasFailures)
            _errors.Add("Expected at least one tool error, but all tool invocations succeeded.");
        return this;
    }

    /// <summary>
    /// Asserts that the total number of tool invocations does not exceed the given limit.
    /// Useful for verifying circuit-breaker thresholds.
    /// </summary>
    public ToolUsageAssertions HaveCallCountAtMost(int maxCalls)
    {
        if (_invocations.Count > maxCalls)
            _errors.Add($"Expected at most {maxCalls} tool call(s), but found {_invocations.Count}.");
        return this;
    }

    /// <summary>
    /// Asserts that the specified tool was never called.
    /// </summary>
    public ToolUsageAssertions NotHaveCalledTool(string toolName)
    {
        ArgumentException.ThrowIfNullOrEmpty(toolName);

        var found = _invocations.Any(t =>
            string.Equals(t.ToolName, toolName, StringComparison.OrdinalIgnoreCase));

        if (found)
            _errors.Add($"Expected tool '{toolName}' to NOT have been called, but it was found in the trace.");

        return this;
    }

    /// <summary>
    /// Validates all accumulated assertions and throws if any failed.
    /// Called automatically by <see cref="ToolUsageAssertionExtensions.Should"/> at the end of a chain.
    /// </summary>
    public void Validate()
    {
        if (_errors.Count > 0)
        {
            throw new EvaluationAssertionException(
                $"Tool usage assertions failed ({_errors.Count} violation(s)):\n" +
                string.Join("\n", _errors.Select((e, i) => $"  [{i + 1}] {e}")));
        }
    }

    internal void AddError(string message) => _errors.Add(message);
    internal IReadOnlyList<ToolCallRecord> Invocations => _invocations;
}

/// <summary>
/// Continuation of a fluent assertion chain after <see cref="ToolUsageAssertions.HaveCalledTool"/>.
/// Enables temporal ordering constraints like <c>BeforeTool("BookFlight")</c>.
/// </summary>
public sealed class ToolCallChain
{
    private readonly ToolUsageAssertions _parent;
    private readonly string _currentTool;

    internal ToolCallChain(ToolUsageAssertions parent, string currentTool)
    {
        _parent = parent;
        _currentTool = currentTool;
    }

    /// <summary>
    /// Asserts that the current tool was invoked before the specified tool.
    /// </summary>
    public ToolCallChain BeforeTool(string laterTool)
    {
        ArgumentException.ThrowIfNullOrEmpty(laterTool);

        var firstCurrent = -1;
        var firstLater = -1;

        for (var i = 0; i < _parent.Invocations.Count; i++)
        {
            var name = _parent.Invocations[i].ToolName;
            if (firstCurrent < 0 && string.Equals(name, _currentTool, StringComparison.OrdinalIgnoreCase))
                firstCurrent = i;
            if (firstLater < 0 && string.Equals(name, laterTool, StringComparison.OrdinalIgnoreCase))
                firstLater = i;
        }

        if (firstCurrent < 0)
        {
            _parent.AddError($"Cannot verify ordering: tool '{_currentTool}' was not found in the trace.");
        }
        else if (firstLater < 0)
        {
            _parent.AddError($"Cannot verify ordering: tool '{laterTool}' was not found in the trace.");
        }
        else if (firstCurrent >= firstLater)
        {
            _parent.AddError(
                $"Expected '{_currentTool}' (index {firstCurrent}) to be called before '{laterTool}' (index {firstLater}).");
        }

        return this;
    }

    /// <summary>
    /// Returns to the parent assertion context for additional chaining.
    /// </summary>
    public ToolUsageAssertions And() => _parent;
}
