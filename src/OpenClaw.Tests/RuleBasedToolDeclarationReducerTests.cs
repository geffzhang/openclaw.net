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

    [Fact]
    public async Task ReduceAsync_BackfillsBelowMinScoreOnlyToMinTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("read_file", "Read a file from disk", "path"),
            Tool("irrelevant_alpha", "Unrelated helper", "alpha"),
            Tool("irrelevant_beta", "Unrelated helper", "beta"),
            Tool("irrelevant_gamma", "Unrelated helper", "gamma")
        };

        var result = await reducer.ReduceAsync(Context(tools, "please use read_file", maxTools: 4, minTools: 2, minScore: 0.5), TestContext.Current.CancellationToken);

        Assert.Equal(["read_file", "irrelevant_alpha"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(2, result.Diagnostics.SelectedCount);
        Assert.Equal(0.0, result.Diagnostics.Scores["irrelevant_alpha"]);
    }

    [Fact]
    public async Task ReduceAsync_RouteToolsDisabledReturnsNoTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("read_file", "Read a file from disk", "path"),
            Tool("message", "Send a message", "text")
        };
        var context = Context(tools, "read_file message", maxTools: 2);
        context.Session.RouteToolsDisabled = true;
        context.Config.AlwaysIncludeTools = ["read_file"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.Tools);
        Assert.Empty(result.Diagnostics.SelectedTools);
        Assert.Empty(result.Diagnostics.PinnedTools);
        Assert.Equal(0, result.Diagnostics.SelectedCount);
    }

    [Fact]
    public async Task ReduceAsync_RouteAndPresetAllowlistsFilterScoredAndPinnedTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("read_file", "Read a file from disk", "path"),
            Tool("message", "Send a message", "text"),
            Tool("browser", "Open a web page", "url")
        };
        var preset = new ResolvedToolPreset
        {
            PresetId = "readonly",
            AllowedTools = new HashSet<string>(["message", "browser"], StringComparer.OrdinalIgnoreCase)
        };
        var context = Context(tools, "read_file message browser", maxTools: 3, preset: preset);
        context.Session.RouteAllowedTools = ["read_file", "message"];
        context.Config.AlwaysIncludeTools = ["read_file", "browser", "message"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(["message"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(["message"], result.Diagnostics.PinnedTools);
        Assert.Equal(["read_file", "browser"], result.Diagnostics.SkippedPinnedTools);
    }

    [Fact]
    public async Task ReduceAsync_RouteAllowlistUsesExactToolIdentity()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("READ_FILE", "Read a file from disk", "path")
        };
        var context = Context(tools, "READ_FILE", maxTools: 1);
        context.Session.RouteAllowedTools = ["read_file"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.Tools);
        Assert.DoesNotContain(result.Diagnostics.SelectedTools, static name => string.Equals(name, "READ_FILE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReduceAsync_NeverAutoIncludeBlocksScoredAndBackfillSelection()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("message", "Send a message", "text"),
            Tool("read_file", "Read a file from disk", "path")
        };
        var context = Context(tools, "send a message", maxTools: 2, minTools: 2);
        context.Config.NeverAutoIncludeTools = ["message"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(["read_file"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.DoesNotContain(result.Tools, static tool => string.Equals(tool.Name, "message", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReduceAsync_NeverAutoIncludeDoesNotBlockAllowedAlwaysIncludeTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("message", "Send a message", "text"),
            Tool("read_file", "Read a file from disk", "path")
        };
        var context = Context(tools, "read_file", maxTools: 2);
        context.Session.RouteAllowedTools = ["message", "read_file"];
        context.Config.AlwaysIncludeTools = ["message"];
        context.Config.NeverAutoIncludeTools = ["message"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Contains("message", result.Tools.Select(static item => item.Name), StringComparer.Ordinal);
        Assert.Equal(["message"], result.Diagnostics.PinnedTools);
    }

    private static ToolDeclarationReductionContext Context(IReadOnlyList<AITool> tools, string prompt, int maxTools, int hardMaxTools = 24, int minTools = 1, double minScore = 0.0, ResolvedToolPreset? preset = null)
    {
        return new ToolDeclarationReductionContext
        {
            Session = new Session { Id = "sess1", ChannelId = "websocket", SenderId = "user1" },
            UserMessage = prompt,
            CandidateTools = tools,
            Preset = preset,
            Config = new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "rule",
                MaxTools = maxTools,
                MinTools = minTools,
                HardMaxTools = hardMaxTools,
                MinScore = minScore
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