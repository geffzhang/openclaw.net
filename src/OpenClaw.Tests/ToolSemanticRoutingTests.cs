using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolSemanticRoutingTests
{
    [Fact]
    public async Task ToolIndex_Search_SortsByCosineAndLimitsTopK()
    {
        var index = CreateIndex();
        await index.InitializeAsync([WeatherTool, EmailTool, FileTool], CancellationToken.None);

        var results = await index.SearchAsync("forecast and weather", ["weather", "email", "read_file"], topK: 2, minScore: 0, mode: "fast", CancellationToken.None);

        Assert.Equal(["weather", "read_file"], results.Select(static item => item.ToolName).ToArray());
    }

    [Fact]
    public async Task ToolIndex_Search_AppliesMinScore()
    {
        var index = CreateIndex();
        await index.InitializeAsync([WeatherTool, EmailTool], CancellationToken.None);

        var results = await index.SearchAsync("forecast", ["weather", "email"], topK: 5, minScore: 0.8f, mode: "fast", CancellationToken.None);

        Assert.Single(results);
        Assert.Equal("weather", results[0].ToolName);
    }

    [Fact]
    public async Task ToolIndex_AddUpdateRemove_ChangesSearchResults()
    {
        var index = CreateIndex();
        await index.InitializeAsync([WeatherTool], CancellationToken.None);

        var beforeAdd = await index.SearchAsync("send mail", ["weather", "email"], topK: 5, minScore: 0, mode: "fast", CancellationToken.None);
        Assert.DoesNotContain(beforeAdd, candidate => candidate.ToolName == "email");

        await index.AddOrUpdateToolAsync(EmailTool, CancellationToken.None);
        var afterAdd = await index.SearchAsync("send mail", ["weather", "email"], topK: 5, minScore: 0, mode: "fast", CancellationToken.None);
        Assert.Equal("email", afterAdd[0].ToolName);

        Assert.True(index.RemoveTool("email"));
        var afterRemove = await index.SearchAsync("send mail", ["weather", "email"], topK: 5, minScore: 0, mode: "fast", CancellationToken.None);
        Assert.DoesNotContain(afterRemove, candidate => candidate.ToolName == "email");
    }

    [Fact]
    public async Task ToolIndex_Search_UsesQueryCache()
    {
        var generator = new KeywordEmbeddingGenerator();
        var index = new ToolIndex(new ToolSemanticRoutingConfig { QueryCacheSize = 8 }, generator);
        await index.InitializeAsync([WeatherTool], CancellationToken.None);
        var callsAfterInitialize = generator.CallCount;

        await index.SearchAsync("forecast", ["weather"], topK: 5, minScore: 0, mode: "fast", CancellationToken.None);
        var callsAfterFirstQuery = generator.CallCount;
        await index.SearchAsync("forecast", ["weather"], topK: 5, minScore: 0, mode: "fast", CancellationToken.None);

        Assert.Equal(callsAfterInitialize + 1, callsAfterFirstQuery);
        Assert.Equal(callsAfterFirstQuery, generator.CallCount);
    }

    [Fact]
    public async Task ToolIndex_SearchAndUpdate_CanRunConcurrently()
    {
        var index = CreateIndex();
        await index.InitializeAsync([WeatherTool, EmailTool], CancellationToken.None);

        var searches = Enumerable.Range(0, 20)
            .Select(_ => index.SearchAsync("forecast", ["weather", "email"], topK: 2, minScore: 0, mode: "balanced", CancellationToken.None).AsTask());
        var updates = Enumerable.Range(0, 10)
            .Select(i => index.AddOrUpdateToolAsync(FileTool with { DefinitionHash = "file-" + i }, CancellationToken.None).AsTask());

        await Task.WhenAll(searches.Concat(updates));
    }

    [Fact]
    public async Task ToolIndex_StaleConcurrentUpdate_CannotOverwriteNewerUpdate()
    {
        var generator = new BlockingEmbeddingGenerator();
        var index = new ToolIndex(new ToolSemanticRoutingConfig { QueryCacheSize = 16 }, generator);
        await index.InitializeAsync([WeatherTool], CancellationToken.None);

        var stale = WeatherTool with
        {
            DefinitionHash = "weather-stale",
            EmbeddingText = "stale-route"
        };
        var fresh = WeatherTool with
        {
            DefinitionHash = "weather-fresh",
            EmbeddingText = "fresh-route"
        };

        var staleUpdate = index.AddOrUpdateToolAsync(stale, CancellationToken.None).AsTask();
        await generator.StaleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await index.AddOrUpdateToolAsync(fresh, CancellationToken.None);
        generator.ReleaseStale.SetResult();
        await staleUpdate;

        var results = await index.SearchAsync("forecast", ["weather"], topK: 1, minScore: 0.9f, mode: "fast", CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("weather", result.ToolName);
        Assert.True(result.Score >= 0.9f);
    }

    [Fact]
    public async Task ToolRouter_OnlyReturnsCandidateTools()
    {
        var router = CreateRouter();
        var results = await router.RouteAsync(
            "send mail",
            [WeatherTool, EmailTool],
            ["weather"],
            new ToolSemanticRoutingConfig { Enabled = true, TopK = 5 },
            CancellationToken.None);

        Assert.Equal(["weather"], results.Select(static item => item.ToolName).ToArray());
    }

    [Fact]
    public async Task ToolRouter_EmbeddingFailure_RespectsFailOpen()
    {
        var router = new ToolRouter(
            new ToolIndex(new ToolSemanticRoutingConfig(), new ThrowingEmbeddingGenerator()),
            NullLogger<ToolRouter>.Instance);
        var config = new ToolSemanticRoutingConfig { Enabled = true, TopK = 1, FailOpen = true };

        var openResults = await router.RouteAsync("forecast", [WeatherTool, EmailTool], ["weather", "email"], config, CancellationToken.None);
        Assert.Equal(["weather", "email"], openResults.Select(static item => item.ToolName).ToArray());

        config.FailOpen = false;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await router.RouteAsync("forecast", [WeatherTool, EmailTool], ["weather", "email"], config, CancellationToken.None));
    }

    [Fact]
    public async Task OpenClawToolExecutor_SemanticRoutingCannotReAddPresetDeniedTool()
    {
        var weather = new TestTool("weather", "Weather forecast");
        var email = new TestTool("email", "Send email");
        var filter = new ReaddingFilter(["email", "weather"]);
        var executor = new OpenClawToolExecutor(
            [weather, email],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            config: new GatewayConfig(),
            toolPresetResolver: new StaticPresetResolver(["weather"]),
            toolDeclarationFilter: filter);

        var declarations = await executor.GetToolDeclarationsAsync(new Session { Id = "s", ChannelId = "c", SenderId = "u" }, "send email", CancellationToken.None);

        Assert.Equal(["weather"], declarations.Select(static item => item.Name).ToArray());
        Assert.Equal(["weather"], filter.LastCandidates);
    }

    [Fact]
    public async Task OpenClawToolExecutor_SemanticRoutingDisabledKeepsExistingBehavior()
    {
        var executor = new OpenClawToolExecutor(
            [new TestTool("weather", "Weather forecast"), new TestTool("email", "Send email")],
            toolTimeoutSeconds: 5,
            requireToolApproval: false,
            approvalRequiredTools: [],
            hooks: [],
            config: new GatewayConfig(),
            toolDeclarationFilter: null);

        var declarations = await executor.GetToolDeclarationsAsync(new Session { Id = "s", ChannelId = "c", SenderId = "u" }, "forecast", CancellationToken.None);

        Assert.Equal(["weather", "email"], declarations.Select(static item => item.Name).ToArray());
    }

    private static ToolIndex CreateIndex()
        => new(new ToolSemanticRoutingConfig { QueryCacheSize = 16 }, new KeywordEmbeddingGenerator());

    private static ToolRouter CreateRouter()
        => new(CreateIndex(), NullLogger<ToolRouter>.Instance);

    private static ToolDefinitionSnapshot WeatherTool { get; } = new(
        "weather",
        "Weather forecast lookup",
        """{"type":"object","properties":{"location":{"description":"City"}}}""",
        "weather forecast temperature",
        "weather-v1");

    private static ToolDefinitionSnapshot EmailTool { get; } = new(
        "email",
        "Send mail messages",
        """{"type":"object","properties":{"to":{"description":"Recipient"}}}""",
        "email mail send recipient",
        "email-v1");

    private static ToolDefinitionSnapshot FileTool { get; } = new(
        "read_file",
        "Read files",
        """{"type":"object","properties":{"path":{"description":"File path"}}}""",
        "file read path",
        "file-v1");

    private sealed class KeywordEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public EmbeddingGeneratorMetadata Metadata { get; } = new("keyword-test");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                Interlocked.Increment(ref _callCount);
                generated.Add(new Embedding<float>(Embed(value)));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static float[] Embed(string value)
        {
            var text = value.ToLowerInvariant();
            if (text.Contains("weather") || text.Contains("forecast"))
                return [1f, 0f, 0.2f];
            if (text.Contains("email") || text.Contains("mail"))
                return [0f, 1f, 0f];
            if (text.Contains("file") || text.Contains("path"))
                return [0.3f, 0f, 1f];
            return [0f, 0f, 0f];
        }
    }

    private sealed class ThrowingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public EmbeddingGeneratorMetadata Metadata { get; } = new("throwing-test");

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("embedding failed");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class BlockingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public TaskCompletionSource StaleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStale { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EmbeddingGeneratorMetadata Metadata { get; } = new("blocking-test");

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var generated = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var value in values)
            {
                if (value.Contains("stale-route", StringComparison.OrdinalIgnoreCase))
                {
                    StaleStarted.TrySetResult();
                    await ReleaseStale.Task.WaitAsync(cancellationToken);
                }

                generated.Add(new Embedding<float>(Embed(value)));
            }

            return generated;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static float[] Embed(string value)
        {
            if (value.Contains("fresh-route", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("forecast", StringComparison.OrdinalIgnoreCase))
            {
                return [1f, 0f];
            }

            if (value.Contains("stale-route", StringComparison.OrdinalIgnoreCase))
                return [0f, 1f];

            return [0.5f, 0.5f];
        }
    }

    private sealed class ReaddingFilter(IReadOnlyList<string> selected) : IToolDeclarationFilter
    {
        public IReadOnlyList<string> LastCandidates { get; private set; } = [];

        public ValueTask<IReadOnlyList<string>> FilterToolNamesAsync(
            Session session,
            string userPrompt,
            IReadOnlyList<ToolDefinitionSnapshot> tools,
            IReadOnlyCollection<string> candidateToolNames,
            CancellationToken ct)
        {
            _ = session;
            _ = userPrompt;
            _ = tools;
            _ = ct;
            LastCandidates = candidateToolNames.ToArray();
            return ValueTask.FromResult(selected);
        }
    }

    private sealed class StaticPresetResolver : IToolPresetResolver
    {
        private readonly IReadOnlySet<string> _allowedTools;

        public StaticPresetResolver(IEnumerable<string> allowedTools)
            => _allowedTools = allowedTools.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public ResolvedToolPreset Resolve(Session session, IEnumerable<string> availableToolNames)
            => new()
            {
                PresetId = "test",
                AllowedTools = _allowedTools
            };

        public IReadOnlyList<ResolvedToolPreset> ListPresets(IEnumerable<string> availableToolNames)
            => [Resolve(new Session { Id = "list", ChannelId = "test", SenderId = "test" }, availableToolNames)];
    }

    private sealed class TestTool(string name, string description) : ITool
    {
        public string Name { get; } = name;

        public string Description { get; } = description;

        public string ParameterSchema => """{"type":"object","properties":{"value":{"type":"string"}}}""";

        public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        {
            _ = argumentsJson;
            _ = ct;
            return ValueTask.FromResult("ok");
        }
    }
}
