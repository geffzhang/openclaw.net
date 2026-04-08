using OpenClaw.Evaluation.Assertions;
using OpenClaw.Evaluation.Models;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Tests for the fluent tool-usage assertion API.
/// </summary>
public sealed class EvaluationToolUsageAssertionTests
{
    private static IReadOnlyList<ToolCallRecord> CreateSampleInvocations() =>
    [
        new ToolCallRecord { ToolName = "SearchFlights", IsSuccessful = true, DurationMilliseconds = 100, StartedAtUtc = DateTimeOffset.UtcNow },
        new ToolCallRecord { ToolName = "FilterResults", IsSuccessful = true, DurationMilliseconds = 50, StartedAtUtc = DateTimeOffset.UtcNow },
        new ToolCallRecord { ToolName = "BookFlight", IsSuccessful = true, DurationMilliseconds = 200, StartedAtUtc = DateTimeOffset.UtcNow }
    ];

    [Fact]
    public void HaveCalledTool_Succeeds_WhenToolExists()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should().HaveCalledTool("SearchFlights").And().Validate();
    }

    [Fact]
    public void HaveCalledTool_Fails_WhenToolMissing()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should().HaveCalledTool("NonExistentTool").And().Validate());

        Assert.Contains("NonExistentTool", ex.Message);
    }

    [Fact]
    public void BeforeTool_Succeeds_WhenOrderIsCorrect()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should()
            .HaveCalledTool("SearchFlights")
            .BeforeTool("BookFlight")
            .And()
            .Validate();
    }

    [Fact]
    public void BeforeTool_Fails_WhenOrderIsWrong()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should()
                .HaveCalledTool("BookFlight")
                .BeforeTool("SearchFlights")
                .And()
                .Validate());

        Assert.Contains("BookFlight", ex.Message);
        Assert.Contains("SearchFlights", ex.Message);
    }

    [Fact]
    public void HaveNoErrors_Succeeds_WhenAllToolsSucceed()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should().HaveNoErrors().Validate();
    }

    [Fact]
    public void HaveNoErrors_Fails_WhenToolFailed()
    {
        var invocations = new List<ToolCallRecord>
        {
            new() { ToolName = "shell", IsSuccessful = false, ErrorMessage = "Permission denied", StartedAtUtc = DateTimeOffset.UtcNow }
        };
        var trace = new AgentExecutionTrace("test", invocations);

        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should().HaveNoErrors().Validate());

        Assert.Contains("shell", ex.Message);
    }

    [Fact]
    public void HaveErrors_Succeeds_WhenToolFailed()
    {
        var invocations = new List<ToolCallRecord>
        {
            new() { ToolName = "shell", IsSuccessful = false, StartedAtUtc = DateTimeOffset.UtcNow }
        };
        var trace = new AgentExecutionTrace("test", invocations);
        trace.Should().HaveErrors().Validate();
    }

    [Fact]
    public void HaveErrors_Fails_WhenAllToolsSucceeded()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should().HaveErrors().Validate());

        Assert.Contains("all tool invocations succeeded", ex.Message);
    }

    [Fact]
    public void HaveCallCountAtMost_Succeeds_WhenUnderLimit()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should().HaveCallCountAtMost(5).Validate();
    }

    [Fact]
    public void HaveCallCountAtMost_Fails_WhenOverLimit()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should().HaveCallCountAtMost(2).Validate());

        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void NotHaveCalledTool_Succeeds_WhenToolAbsent()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should().NotHaveCalledTool("shell").Validate();
    }

    [Fact]
    public void NotHaveCalledTool_Fails_WhenToolPresent()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should().NotHaveCalledTool("SearchFlights").Validate());

        Assert.Contains("SearchFlights", ex.Message);
    }

    [Fact]
    public void ComplexChain_MultipleAssertions_AllPass()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should()
            .HaveCalledTool("SearchFlights")
            .BeforeTool("FilterResults")
            .BeforeTool("BookFlight")
            .And()
            .HaveNoErrors()
            .HaveCallCountAtMost(10)
            .NotHaveCalledTool("shell")
            .Validate();
    }

    [Fact]
    public void ComplexChain_MultipleFailures_ReportsAll()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        var ex = Assert.Throws<EvaluationAssertionException>(() =>
            trace.Should()
                .HaveCalledTool("MissingTool")
                .And()
                .HaveCallCountAtMost(1)
                .Validate());

        Assert.Contains("MissingTool", ex.Message);
        Assert.Contains("3", ex.Message); // actual count
        Assert.Contains("2 violation(s)", ex.Message);
    }

    [Fact]
    public void Should_OnEvaluationResult_Works()
    {
        var result = new EvaluationResult
        {
            Trace = new AgentExecutionTrace("test", CreateSampleInvocations())
        };

        result.Should()
            .HaveCalledTool("SearchFlights")
            .And()
            .HaveNoErrors()
            .Validate();
    }

    [Fact]
    public void Should_OnEvaluationResult_ThrowsWhenTraceNull()
    {
        var result = new EvaluationResult { Trace = null };
        Assert.Throws<InvalidOperationException>(() => result.Should());
    }

    [Fact]
    public void EvaluationResult_MeetsQualityGate_AllScoresAboveThreshold()
    {
        var result = new EvaluationResult
        {
            FaithfulnessScore = 0.95,
            ContextRelevanceScore = 0.90,
            FluencyScore = 0.88
        };

        Assert.True(result.MeetsQualityGate(0.85));
        Assert.False(result.MeetsQualityGate(0.92));
    }

    [Fact]
    public void EvaluationResult_MeetsQualityGate_PartialScores()
    {
        var result = new EvaluationResult
        {
            FaithfulnessScore = 0.95
            // Other scores not set
        };

        Assert.True(result.MeetsQualityGate(0.90));
    }

    [Fact]
    public void HaveCalledTool_IsCaseInsensitive()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should().HaveCalledTool("searchflights").And().Validate();
    }

    [Fact]
    public void BeforeTool_IsCaseInsensitive()
    {
        var trace = new AgentExecutionTrace("test", CreateSampleInvocations());
        trace.Should()
            .HaveCalledTool("searchflights")
            .BeforeTool("bookflight")
            .And()
            .Validate();
    }

    [Fact]
    public void AgentExecutionTrace_Properties_ComputeCorrectly()
    {
        var invocations = new List<ToolCallRecord>
        {
            new() { ToolName = "a", IsSuccessful = true, StartedAtUtc = DateTimeOffset.UtcNow },
            new() { ToolName = "b", IsSuccessful = false, IsTimedOut = true, StartedAtUtc = DateTimeOffset.UtcNow },
            new() { ToolName = "c", IsSuccessful = false, StartedAtUtc = DateTimeOffset.UtcNow }
        };
        var trace = new AgentExecutionTrace("props-test", invocations);

        Assert.False(trace.AllToolsSucceeded);
        Assert.Equal(2, trace.FailedToolCount);
        Assert.Equal(1, trace.TimedOutToolCount);
    }
}
