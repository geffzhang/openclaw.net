using System.Text.Json;
using OpenClaw.MicrosoftAgentFrameworkAdapter;
using Xunit;

namespace OpenClaw.Tests;

public sealed class MicrosoftAgentFrameworkInteropTests
{
    [Fact]
    public async Task EntrypointTool_InvokesRunner_AndReturnsTextByDefault()
    {
        var runner = new FakeRunner(_ => new MicrosoftAgentFrameworkToolResult
        {
            Text = "ok",
            ThreadId = "t-1",
            StateJson = """{"step":1}"""
        });

        var tool = new MicrosoftAgentFrameworkEntrypointTool(runner, new MicrosoftAgentFrameworkInteropOptions
        {
            AllowedAgents = ["planner"]
        });

        var result = await tool.ExecuteAsync("""{"agent":"planner","input":"hello"}""", CancellationToken.None);

        Assert.Equal("ok", result);
        Assert.NotNull(runner.LastRequest);
        Assert.Equal("planner", runner.LastRequest!.Agent);
        Assert.Equal("hello", runner.LastRequest.Input);
    }

    [Fact]
    public async Task EntrypointTool_RejectsDisallowedAgent()
    {
        var runner = new FakeRunner(_ => new MicrosoftAgentFrameworkToolResult { Text = "ok" });
        var tool = new MicrosoftAgentFrameworkEntrypointTool(runner, new MicrosoftAgentFrameworkInteropOptions
        {
            AllowedAgents = ["writer"]
        });

        var result = await tool.ExecuteAsync("""{"agent":"planner","input":"hello"}""", CancellationToken.None);

        Assert.Equal("Error: agent is not allowed.", result);
        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task EntrypointTool_ReturnsJsonEnvelope_WhenRequested()
    {
        var runner = new FakeRunner(_ => new MicrosoftAgentFrameworkToolResult
        {
            Text = "hello from agent",
            ThreadId = "thread-7",
            StateJson = """{"phase":"done"}"""
        });
        var tool = new MicrosoftAgentFrameworkEntrypointTool(runner);

        var result = await tool.ExecuteAsync("""{"agent":"planner","input":"hello","format":"json"}""", CancellationToken.None);
        using var doc = JsonDocument.Parse(result);

        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("planner", doc.RootElement.GetProperty("agent").GetString());
        Assert.Equal("thread-7", doc.RootElement.GetProperty("thread_id").GetString());
        Assert.Equal("hello from agent", doc.RootElement.GetProperty("text").GetString());
    }

    private sealed class FakeRunner(Func<MicrosoftAgentFrameworkToolRequest, MicrosoftAgentFrameworkToolResult> handler)
        : IMicrosoftAgentFrameworkRunner
    {
        public MicrosoftAgentFrameworkToolRequest? LastRequest { get; private set; }

        public ValueTask<MicrosoftAgentFrameworkToolResult> InvokeAsync(MicrosoftAgentFrameworkToolRequest request, CancellationToken ct)
        {
            LastRequest = request;
            return new(handler(request));
        }
    }
}
