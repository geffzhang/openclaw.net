using System.Diagnostics;
using OpenClaw.Core.Observability;
using OpenClaw.Evaluation.Models;
using OpenClaw.Evaluation.Telemetry;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for the telemetry bridge: <see cref="InMemoryTraceCollector"/> and
/// <see cref="ActivityTraceExtractor"/>.
/// </summary>
public sealed class EvaluationTraceExtractorTests : IDisposable
{
    private readonly InMemoryTraceCollector _collector;

    public EvaluationTraceExtractorTests()
    {
        _collector = new InMemoryTraceCollector(Telemetry.ServiceName);
    }

    public void Dispose() => _collector.Dispose();

    [Fact]
    public void ExtractTrace_ReturnsEmpty_WhenNoActivitiesForSession()
    {
        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "nonexistent");

        Assert.Equal("nonexistent", trace.SessionId);
        Assert.Empty(trace.ToolInvocations);
        Assert.Equal(0, trace.LlmCallCount);
    }

    [Fact]
    public void ExtractTrace_CapturesToolActivities_WithSessionTag()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool web_search"))
        {
            activity?.SetTag("session.id", "sess-1");
            activity?.SetTag("ok", "True");
            activity?.SetTag("tool.name", "web_search");
        }

        using (var activity = Telemetry.ActivitySource.StartActivity("Tool file_read"))
        {
            activity?.SetTag("session.id", "sess-1");
            activity?.SetTag("ok", "True");
            activity?.SetTag("tool.name", "file_read");
        }

        // Activity for a different session - should not appear
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool shell"))
        {
            activity?.SetTag("session.id", "sess-2");
            activity?.SetTag("ok", "True");
            activity?.SetTag("tool.name", "shell");
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "sess-1");

        Assert.Equal("sess-1", trace.SessionId);
        Assert.Equal(2, trace.ToolInvocations.Count);
        Assert.Equal("web_search", trace.ToolInvocations[0].ToolName);
        Assert.Equal("file_read", trace.ToolInvocations[1].ToolName);
        Assert.True(trace.AllToolsSucceeded);
    }

    [Fact]
    public void ExtractTrace_ParsesFailedTools()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool shell"))
        {
            activity?.SetTag("session.id", "sess-fail");
            activity?.SetTag("tool.name", "shell");
            activity?.SetTag("error.message", "Permission denied");
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "sess-fail");

        Assert.Single(trace.ToolInvocations);
        var tool = trace.ToolInvocations[0];
        Assert.Equal("shell", tool.ToolName);
        Assert.False(tool.IsSuccessful);
        Assert.Equal("Permission denied", tool.ErrorMessage);
        Assert.Equal(1, trace.FailedToolCount);
    }

    [Fact]
    public void ExtractTrace_ParsesTimedOutTools()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool slow_tool"))
        {
            activity?.SetTag("session.id", "sess-timeout");
            activity?.SetTag("tool.name", "slow_tool");
            activity?.SetTag("timed_out", "true");
            activity?.SetTag("ok", "True");
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "sess-timeout");

        Assert.Single(trace.ToolInvocations);
        Assert.True(trace.ToolInvocations[0].IsTimedOut);
        Assert.Equal(1, trace.TimedOutToolCount);
    }

    [Fact]
    public void ExtractTrace_ParsesTokenTags()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("LLM Chat"))
        {
            activity?.SetTag("session.id", "sess-tokens");
            activity?.SetTag("input.tokens", "150");
            activity?.SetTag("output.tokens", "80");
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "sess-tokens");

        Assert.Equal(1, trace.LlmCallCount);
        Assert.Equal(150, trace.TotalInputTokens);
        Assert.Equal(80, trace.TotalOutputTokens);
    }

    [Fact]
    public void ExtractAllTraces_GroupsBySession()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool a"))
        {
            activity?.SetTag("session.id", "all-sess-1");
            activity?.SetTag("tool.name", "a");
        }

        using (var activity = Telemetry.ActivitySource.StartActivity("Tool b"))
        {
            activity?.SetTag("session.id", "all-sess-2");
            activity?.SetTag("tool.name", "b");
        }

        var traces = ActivityTraceExtractor.ExtractAllTraces(_collector.GetActivities());

        Assert.True(traces.Count >= 2);
        Assert.Contains(traces, t => t.SessionId == "all-sess-1");
        Assert.Contains(traces, t => t.SessionId == "all-sess-2");
    }

    [Fact]
    public void ExtractTrace_HandlesLegacySessionTag()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool legacy"))
        {
            activity?.SetTag("session", "legacy-sess");
            activity?.SetTag("tool.name", "legacy_tool");
            activity?.SetTag("ok", "True");
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "legacy-sess");

        Assert.Single(trace.ToolInvocations);
        Assert.Equal("legacy_tool", trace.ToolInvocations[0].ToolName);
    }

    [Fact]
    public void ExtractTrace_ExtractsToolNameFromOperationName()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool code_exec"))
        {
            activity?.SetTag("session.id", "sess-opname");
            activity?.SetTag("ok", "True");
            // No tool.name tag; should fall back to operation name parsing
        }

        var trace = ActivityTraceExtractor.ExtractTrace(_collector.GetActivities(), "sess-opname");

        Assert.Single(trace.ToolInvocations);
        Assert.Equal("code_exec", trace.ToolInvocations[0].ToolName);
    }

    [Fact]
    public void InMemoryTraceCollector_Clear_RemovesAllActivities()
    {
        using (var activity = Telemetry.ActivitySource.StartActivity("Tool temp"))
        {
            activity?.SetTag("session.id", "sess-clear");
            activity?.SetTag("tool.name", "temp");
        }

        Assert.NotEmpty(_collector.GetActivities());

        _collector.Clear();

        // After clear, newly extracted should be empty
        // (old activities drained, no new ones emitted)
        Assert.Empty(_collector.GetActivities());
    }
}
