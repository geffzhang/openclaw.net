using System.Diagnostics;
using System.Globalization;
using OpenClaw.Evaluation.Models;

namespace OpenClaw.Evaluation.Telemetry;

/// <summary>
/// Transforms raw <see cref="Activity"/> objects captured by <see cref="InMemoryTraceCollector"/>
/// into strongly-typed <see cref="AgentExecutionTrace"/> evaluation models.
/// </summary>
public static class ActivityTraceExtractor
{
    /// <summary>
    /// Extracts an <see cref="AgentExecutionTrace"/> for the specified session from captured activities.
    /// Activities are matched by the "session.id" tag emitted by the OpenClaw runtime.
    /// </summary>
    public static AgentExecutionTrace ExtractTrace(IReadOnlyList<Activity> activities, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(activities);
        ArgumentException.ThrowIfNullOrEmpty(sessionId);

        var sessionActivities = activities
            .Where(a => MatchesSession(a, sessionId))
            .OrderBy(a => a.StartTimeUtc)
            .ToList();

        var toolInvocations = sessionActivities
            .Where(IsToolActivity)
            .Select(MapToolCall)
            .ToList();

        var llmCallCount = sessionActivities.Count(a =>
            a.OperationName.Contains("LLM", StringComparison.OrdinalIgnoreCase) ||
            a.OperationName.Contains("Chat", StringComparison.OrdinalIgnoreCase));

        var totalInputTokens = SumTagValues(sessionActivities, "input.tokens");
        var totalOutputTokens = SumTagValues(sessionActivities, "output.tokens");

        var totalDuration = sessionActivities.Count > 0
            ? sessionActivities[^1].StartTimeUtc + sessionActivities[^1].Duration - sessionActivities[0].StartTimeUtc
            : TimeSpan.Zero;

        return new AgentExecutionTrace(sessionId, toolInvocations)
        {
            LlmCallCount = llmCallCount,
            TotalInputTokens = totalInputTokens,
            TotalOutputTokens = totalOutputTokens,
            TotalDuration = totalDuration
        };
    }

    /// <summary>
    /// Extracts traces for all distinct sessions found in the captured activities.
    /// </summary>
    public static IReadOnlyList<AgentExecutionTrace> ExtractAllTraces(IReadOnlyList<Activity> activities)
    {
        ArgumentNullException.ThrowIfNull(activities);

        var sessionIds = activities
            .Select(a => GetTagValue(a, "session.id"))
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return sessionIds.Select(id => ExtractTrace(activities, id!)).ToList();
    }

    private static bool MatchesSession(Activity activity, string sessionId)
    {
        var tag = GetTagValue(activity, "session.id");
        if (string.Equals(tag, sessionId, StringComparison.Ordinal)) return true;

        // Also check the legacy "session" tag format used by some OpenClaw components
        var legacyTag = GetTagValue(activity, "session");
        return string.Equals(legacyTag, sessionId, StringComparison.Ordinal);
    }

    private static bool IsToolActivity(Activity activity)
        => activity.OperationName.StartsWith("Tool ", StringComparison.OrdinalIgnoreCase)
           || activity.OperationName.StartsWith("tool.", StringComparison.OrdinalIgnoreCase)
           || HasTag(activity, "tool.name");

    private static ToolCallRecord MapToolCall(Activity activity)
    {
        var toolName = GetTagValue(activity, "tool.name")
                       ?? ExtractToolNameFromOperation(activity.OperationName);

        var isSuccessful = GetTagValue(activity, "ok") is "True" or "true"
                           || GetTagValue(activity, "otel.status_code") is "OK"
                           || activity.Status == ActivityStatusCode.Ok
                           || activity.Status == ActivityStatusCode.Unset;

        var isTimedOut = GetTagValue(activity, "timed_out") is "True" or "true";
        var errorMessage = GetTagValue(activity, "error.message")
                           ?? GetTagValue(activity, "otel.status_description");

        if (!string.IsNullOrEmpty(errorMessage))
            isSuccessful = false;

        return new ToolCallRecord
        {
            ToolName = toolName,
            DurationMilliseconds = activity.Duration.TotalMilliseconds,
            IsSuccessful = isSuccessful,
            IsTimedOut = isTimedOut,
            InputTokens = ParseLongTag(activity, "input.tokens"),
            OutputTokens = ParseLongTag(activity, "output.tokens"),
            StartedAtUtc = new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            ErrorMessage = errorMessage
        };
    }

    private static string ExtractToolNameFromOperation(string operationName)
    {
        // Handles "Tool web_search", "tool.web_search", or just "web_search"
        if (operationName.StartsWith("Tool ", StringComparison.OrdinalIgnoreCase))
            return operationName["Tool ".Length..].Trim();
        if (operationName.StartsWith("tool.", StringComparison.OrdinalIgnoreCase))
            return operationName["tool.".Length..].Trim();
        return operationName;
    }

    private static string? GetTagValue(Activity activity, string key)
    {
        foreach (var tag in activity.Tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                return tag.Value;
        }
        return null;
    }

    private static bool HasTag(Activity activity, string key)
    {
        foreach (var tag in activity.Tags)
        {
            if (string.Equals(tag.Key, key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static long ParseLongTag(Activity activity, string key)
    {
        var value = GetTagValue(activity, key);
        return value is not null && long.TryParse(value, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static long SumTagValues(List<Activity> activities, string key)
    {
        long sum = 0;
        foreach (var activity in activities)
        {
            sum += ParseLongTag(activity, key);
        }
        return sum;
    }
}
