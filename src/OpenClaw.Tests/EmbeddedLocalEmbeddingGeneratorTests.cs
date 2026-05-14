using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Extensions;
using Xunit;

namespace OpenClaw.Tests;

public sealed class EmbeddedLocalEmbeddingGeneratorTests
{
    [Fact]
    public async Task GenerateAsync_PostsOpenAiCompatibleEmbeddingRequest()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "data": [
                    { "index": 0, "embedding": [1.0, 0.0, 0.5] },
                    { "index": 1, "embedding": [0.0, 1.0, 0.25] }
                  ],
                  "model": "gemma-local-small-q4"
                }
                """)
            }
        };
        using var httpClient = new HttpClient(handler);
        using var generator = CreateGenerator(httpClient);

        var result = await generator.GenerateAsync(["weather tool", "email tool"], cancellationToken: CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("http://localhost:8080/v1/embeddings", handler.LastRequest.RequestUri!.ToString());
        Assert.NotNull(handler.LastRequestBody);
        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var root = document.RootElement;
        Assert.Equal("gemma-local-small-q4", root.GetProperty("model").GetString());
        var input = root.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        Assert.Equal("weather tool", input[0].GetString());
        Assert.Equal("email tool", input[1].GetString());
        Assert.Equal(2, result.Count);
        Assert.Equal([1.0f, 0.0f, 0.5f], result[0].Vector.ToArray());
        Assert.Equal([0.0f, 1.0f, 0.25f], result[1].Vector.ToArray());
    }

    [Fact]
    public async Task GenerateAsync_PreservesOrderByResponseIndex()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent("""
                {
                  "data": [
                    { "index": 1, "embedding": [0.0, 1.0] },
                    { "index": 0, "embedding": [1.0, 0.0] }
                  ],
                  "model": "gemma-local-small-q4"
                }
                """)
            }
        };
        using var generator = CreateGenerator(new HttpClient(handler));

        var result = await generator.GenerateAsync(["first", "second"], cancellationToken: CancellationToken.None);

        Assert.Equal([1.0f, 0.0f], result[0].Vector.ToArray());
        Assert.Equal([0.0f, 1.0f], result[1].Vector.ToArray());
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("{ \"data\": [] }")]
    [InlineData("{ \"data\": [{ \"index\": 0, \"embedding\": [] }] }")]
    [InlineData("{ \"data\": [{ \"embedding\": [1.0] }] }")]
    [InlineData("{ \"data\": [{ \"index\": 0, \"embedding\": [1.0] }, { \"index\": 0, \"embedding\": [2.0] }] }")]
    [InlineData("{ \"data\": [{ \"index\": 1, \"embedding\": [1.0] }] }")]
    [InlineData("{ \"data\": [{ \"index\": 0, \"embedding\": [1.0] }, { \"index\": 1, \"embedding\": [1.0, 2.0] }] }")]
    public async Task GenerateAsync_RejectsMalformedEmbeddingResponses(string responseJson)
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(responseJson)
            }
        };
        using var generator = CreateGenerator(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await generator.GenerateAsync(["first", "second"], cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task GenerateAsync_RejectsNonSuccessStatusCode()
    {
        var handler = new FakeHttpMessageHandler
        {
            ResponseToReturn = new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("sidecar failed", Encoding.UTF8, "text/plain")
            }
        };
        using var generator = CreateGenerator(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await generator.GenerateAsync(["first"], cancellationToken: CancellationToken.None));
        Assert.Contains("Embedded local embeddings request failed", ex.Message, StringComparison.Ordinal);
    }

    private static EmbeddedLocalEmbeddingGenerator CreateGenerator(HttpClient httpClient)
        => new(
            new ToolSemanticRoutingConfig
            {
                EmbeddingProvider = ToolSemanticRoutingEmbeddingProviders.Embedded,
                EmbeddingModel = "gemma-local-small-q4"
            },
            new LocalInferenceConfig { Enabled = true },
            new FakeLocalInferenceSupervisor(),
            httpClient);

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public HttpResponseMessage ResponseToReturn { get; set; } = new(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return ResponseToReturn;
        }
    }

    private sealed class FakeLocalInferenceSupervisor : LocalInferenceSupervisor
    {
        public FakeLocalInferenceSupervisor() : base(new LocalInferenceConfig())
        {
        }

        public override Task<LocalInferenceEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(new LocalInferenceEndpoint(
                new Uri("http://localhost:8080/"),
                new LocalModelPackageDefinition
                {
                    Id = "fake-id",
                    PresetId = "fake-preset",
                    ModelId = modelId,
                    Capabilities = new ModelCapabilities()
                },
                "/fake/path/model.gguf"));
    }
}
