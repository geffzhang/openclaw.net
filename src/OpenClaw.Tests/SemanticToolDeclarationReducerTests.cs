using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Plugins.ToolDeclarationReduction.Semantic;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SemanticToolDeclarationReducerTests
{
    [Fact]
    public async Task ReduceAsync_SemanticMode_RanksRelevantToolByDescriptionAndParameters()
    {
        var reducer = new SemanticToolDeclarationReducer(NullLogger.Instance);
        var tools = new[]
        {
            Tool("database", "Run database queries", "query connectionString"),
            Tool("browser", "Open a web page", "url"),
            Tool("message", "Send updates to a chat channel", "chatId text")
        };

        var result = await reducer.ReduceAsync(Context(tools, "send this update to the chat", "semantic"), TestContext.Current.CancellationToken);

        Assert.Equal("message", result.Tools[0].Name);
        Assert.Equal("semantic", result.Diagnostics.Mode);
        Assert.Contains("message", result.Diagnostics.Scores.Keys);
    }

    [Fact]
    public async Task ReduceAsync_HybridMode_CombinesExplicitToolNameAndSemanticScore()
    {
        var reducer = new SemanticToolDeclarationReducer(NullLogger.Instance);
        var tools = new[]
        {
            Tool("read_file", "Read files from disk", "path"),
            Tool("apply_patch", "Apply a source code patch", "patch"),
            Tool("message", "Send updates to a chat channel", "chatId text")
        };

        var result = await reducer.ReduceAsync(Context(tools, "use read_file then patch the code", "hybrid"), TestContext.Current.CancellationToken);

        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Contains(result.Tools, tool => tool.Name == "apply_patch");
        Assert.Equal("hybrid", result.Diagnostics.Mode);
    }

    [Fact]
    public async Task ReduceAsync_BackfillsBelowMinScoreToMinTools()
    {
        var reducer = new SemanticToolDeclarationReducer(NullLogger.Instance);
        var tools = new[]
        {
            Tool("read_file", "Read files from disk", "path"),
            Tool("browser", "Open a web page", "url"),
            Tool("database", "Run database queries", "query")
        };
        var context = Context(tools, "use read_file", "semantic");
        context.Config.MinTools = 2;
        context.Config.MinScore = 0.8;

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Tools.Count);
        Assert.Equal("read_file", result.Tools[0].Name);
    }

    [Fact]
    public void PromptIntentDistiller_ExtractsMultipleActionPhrases()
    {
        var phrases = PromptIntentDistiller.DistillActionPhrases("read the config, edit the port, then run tests");

        Assert.Contains(phrases, phrase => phrase.Contains("read", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(phrases, phrase => phrase.Contains("edit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(phrases, phrase => phrase.Contains("run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SemanticPlugin_DoesNotReferenceElBrunoPackagesOrRouter()
    {
        var root = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(Path.Combine(root, "src", "OpenClaw.Plugins.ToolDeclarationReduction.Semantic"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ElBruno", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ModelContextProtocol.MCPToolRouter", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LocalEmbeddings", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("LocalLLMs", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ToolDeclarationReductionContext Context(IReadOnlyList<AITool> tools, string prompt, string mode)
        => new()
        {
            Session = new Session { Id = "sess-semantic", ChannelId = "websocket", SenderId = "user1" },
            UserMessage = prompt,
            CandidateTools = tools,
            Config = new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = mode,
                MaxTools = 2,
                MinTools = 1,
                HardMaxTools = 3,
                MinScore = 0.0,
                EnablePromptDistillation = true
            }
        };

    private static AITool Tool(string name, string description, string parameterNames)
    {
        var properties = string.Join(",", parameterNames.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static parameterName => $"\"{parameterName}\":{{\"type\":\"string\"}}"));
        using var schema = JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{properties}}}}}");
        return AIFunctionFactory.CreateDeclaration(
            name,
            description,
            schema.RootElement.Clone(),
            returnJsonSchema: null);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OpenClaw.Net.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}