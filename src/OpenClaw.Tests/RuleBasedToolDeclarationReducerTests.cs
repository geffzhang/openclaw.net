using Microsoft.Extensions.AI;
using OpenClaw.Agent.ToolDeclarations;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class RuleBasedToolDeclarationReducerTests
{
    [Fact]
    public async Task ReduceAsync_RanksExplicitToolNameFirst()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("read_file", "Read a file from disk", "path"),
            Tool("message", "Send a message", "text"),
            Tool("browser", "Open a web page", "url")
        };

        var result = await reducer.ReduceAsync(Context(tools, "please use read_file on this path", maxTools: 2), TestContext.Current.CancellationToken);

        Assert.Equal(["read_file", "browser"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(3, result.Diagnostics.CandidateCount);
        Assert.Equal(2, result.Diagnostics.SelectedCount);
        Assert.True(result.Diagnostics.Scores["read_file"] > result.Diagnostics.Scores["browser"]);
    }

    [Fact]
    public async Task ReduceAsync_UsesParameterNamesForGenericTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("message", "Send content to a channel", "chatId text"),
            Tool("gateway", "Manage gateway runtime", "operation"),
            Tool("sessions", "Inspect active sessions", "sessionId")
        };

        var result = await reducer.ReduceAsync(Context(tools, "send text to chatId", maxTools: 1), TestContext.Current.CancellationToken);

        Assert.Equal(["message"], result.Tools.Select(static item => item.Name).ToArray());
    }

    [Fact]
    public async Task ReduceAsync_AlwaysIncludeCannotExceedHardMax()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = Enumerable.Range(1, 10)
            .Select(index => Tool($"tool_{index}", $"Tool {index}", "value"))
            .ToArray();
        var context = Context(tools, "tool 1", maxTools: 2, hardMaxTools: 3);
        context.Config.AlwaysIncludeTools = ["tool_1", "tool_2", "tool_3", "tool_4"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Tools.Count);
        Assert.Equal(["tool_1", "tool_2", "tool_3"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(["tool_4"], result.Diagnostics.SkippedPinnedTools);
    }

    private static ToolDeclarationReductionContext Context(IReadOnlyList<AITool> tools, string prompt, int maxTools, int hardMaxTools = 24)
    {
        return new ToolDeclarationReductionContext
        {
            Session = new Session { Id = "sess1", ChannelId = "websocket", SenderId = "user1" },
            UserMessage = prompt,
            CandidateTools = tools,
            Config = new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "rule",
                MaxTools = maxTools,
                MinTools = 1,
                HardMaxTools = hardMaxTools,
                MinScore = 0.0
            }
        };
    }

    private static AITool Tool(string name, string description, string parameterNames)
    {
        var properties = string.Join(",", parameterNames.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static name => $"\"{name}\":{{\"type\":\"string\"}}"));
        using var schema = JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{properties}}}}}");
        return AIFunctionFactory.CreateDeclaration(
            name,
            description,
            schema.RootElement.Clone(),
            returnJsonSchema: null);
    }
}