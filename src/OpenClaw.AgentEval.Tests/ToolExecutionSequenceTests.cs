using FluentAssertions;
using OpenClaw.AgentEval.Tests.Infrastructure;
using OpenClaw.Evaluation.Assertions;
using Xunit;

namespace OpenClaw.AgentEval.Tests;

public sealed class ToolExecutionSequenceTests(OpenClawEvaluationHarness harness) : IClassFixture<OpenClawEvaluationHarness>
{
    private readonly OpenClawEvaluationHarness _harness = harness;

    [Fact]
    public async Task FlightAssistant_Should_Execute_Search_Before_Booking()
    {
        var sessionId = $"flight-eval-{Guid.NewGuid():N}";

        var response = await _harness.RunAsync(
            sessionId,
            "请帮我查找下周一从北京飞往东京的最优航班并直接预订。");

        response.Should().Contain("预订");

        var executionTrace = _harness.ExtractExecutionTrace(sessionId);

        executionTrace.ToolInvocations.Should().HaveCount(2);
        executionTrace.ToolInvocations.Select(static invocation => invocation.ToolName)
            .Should()
            .ContainInOrder("SearchFlights", "BookFlight");

        ToolUsageAssertionExtensions.Should(executionTrace)
            .HaveCalledTool("SearchFlights")
            .BeforeTool("BookFlight")
            .And()
            .HaveNoErrors()
            .HaveCallCountAtMost(2)
            .Validate();

        _harness.InterceptedActivities
            .Where(static activity => activity.OperationName == "Agent.ExecuteTool")
            .Should()
            .OnlyContain(activity =>
                activity.Tags.Any(tag => tag.Key == "session.id" && tag.Value == sessionId)
                && activity.Tags.Any(tag => tag.Key == "ok" && tag.Value == bool.TrueString));
    }
}
