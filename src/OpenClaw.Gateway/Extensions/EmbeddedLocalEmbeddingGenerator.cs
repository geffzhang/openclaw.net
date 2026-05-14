using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Extensions;

internal sealed class EmbeddedLocalEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private const string DefaultModelId = "gemma-local-small-q4";
    private readonly string _modelId;
    private readonly LocalInferenceSupervisor _supervisor;
    private readonly HttpClient _httpClient;

    public EmbeddedLocalEmbeddingGenerator(
        ToolSemanticRoutingConfig config,
        LocalInferenceConfig localInference,
        LocalInferenceSupervisor? supervisor = null,
        HttpClient? httpClient = null,
        ILogger<EmbeddedLocalEmbeddingGenerator>? logger = null)
    {
        _modelId = string.IsNullOrWhiteSpace(config.EmbeddingModel) ? DefaultModelId : config.EmbeddingModel.Trim();
        _supervisor = supervisor ?? new LocalInferenceSupervisor(localInference, logger, enableEmbeddings: true);
        _httpClient = httpClient ?? HttpClientFactory.Create(allowAutoRedirect: false);
    }

    public EmbeddingGeneratorMetadata Metadata { get; } = new("openclaw-embedded-local");

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = values.ToArray();
        var generated = new GeneratedEmbeddings<Embedding<float>>();
        if (inputs.Length == 0)
            return generated;

        var modelId = string.IsNullOrWhiteSpace(options?.ModelId) ? _modelId : options.ModelId!.Trim();
        var endpoint = await _supervisor.EnsureRunningAsync(modelId, cancellationToken).ConfigureAwait(false);
        using var request = BuildRequest(endpoint, modelId, inputs);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        foreach (var vector in ParseEmbeddings(document.RootElement, inputs.Length))
            generated.Add(new Embedding<float>(vector));

        return generated;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType == typeof(LocalInferenceSupervisor)
            ? _supervisor
            : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        _httpClient.Dispose();
        _supervisor.Dispose();
    }

    private static HttpRequestMessage BuildRequest(LocalInferenceEndpoint endpoint, string modelId, IReadOnlyList<string> inputs)
    {
        var inputArray = new JsonArray(inputs.Select(static input => (JsonNode?)JsonValue.Create(input)).ToArray());
        var payload = new JsonObject
        {
            ["model"] = modelId,
            ["input"] = inputArray
        };

        return new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "v1/embeddings"))
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
    }

    private static async Task<InvalidOperationException> CreateFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var trimmed = body.Length > 256 ? body[..256] : body;
        return new InvalidOperationException(
            $"Embedded local embeddings request failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {trimmed}");
    }

    private static float[][] ParseEmbeddings(JsonElement root, int expectedCount)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Embedded local embeddings response must contain a data array.");

        var vectors = new float[expectedCount][];
        var dimensions = 0;

        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("index", out var indexElement) || !indexElement.TryGetInt32(out var index))
                throw new InvalidOperationException("Embedded local embeddings response item is missing a numeric index.");
            if (index < 0 || index >= expectedCount)
                throw new InvalidOperationException($"Embedded local embeddings response index {index} is out of range.");
            if (vectors[index] is not null)
                throw new InvalidOperationException($"Embedded local embeddings response contains duplicate index {index}.");
            if (!item.TryGetProperty("embedding", out var embeddingElement) || embeddingElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Embedded local embeddings response item is missing an embedding array.");

            var vector = ParseVector(embeddingElement);
            if (dimensions == 0)
                dimensions = vector.Length;
            else if (vector.Length != dimensions)
                throw new InvalidOperationException("Embedded local embeddings response contained inconsistent vector dimensions.");

            vectors[index] = vector;
        }

        for (var i = 0; i < vectors.Length; i++)
        {
            if (vectors[i] is null)
                throw new InvalidOperationException($"Embedded local embeddings response is missing index {i}.");
        }

        return vectors;
    }

    private static float[] ParseVector(JsonElement embeddingElement)
    {
        var vector = embeddingElement.EnumerateArray().Select(ReadSingle).ToArray();
        if (vector.Length == 0)
            throw new InvalidOperationException("Embedded local embeddings response contained an empty vector.");
        return vector;
    }

    private static float ReadSingle(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Number || !element.TryGetSingle(out var value))
            throw new InvalidOperationException("Embedded local embeddings vector contains a non-numeric value.");
        return value;
    }
}
