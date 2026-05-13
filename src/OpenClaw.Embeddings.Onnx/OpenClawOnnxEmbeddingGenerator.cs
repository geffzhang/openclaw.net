using ElBruno.LocalEmbeddings;
using ElBruno.LocalEmbeddings.Options;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace OpenClaw.Embeddings.Onnx;

public sealed class OpenClawOnnxEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly OpenClawOnnxEmbeddingOptions _options;
    private readonly ILogger? _logger;
    private readonly Lazy<Task<IEmbeddingGenerator<string, Embedding<float>>>> _inner;

    public OpenClawOnnxEmbeddingGenerator(
        OpenClawOnnxEmbeddingOptions options,
        ILogger<OpenClawOnnxEmbeddingGenerator>? logger = null)
    {
        _options = options;
        _logger = logger;
        _inner = new Lazy<Task<IEmbeddingGenerator<string, Embedding<float>>>>(
            CreateGeneratorAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public EmbeddingGeneratorMetadata Metadata { get; } = new("openclaw-onnx");

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var generator = await _inner.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await generator.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
        if (!_inner.IsValueCreated || !_inner.Value.IsCompletedSuccessfully)
            return;

        if (_inner.Value.Result is IDisposable disposable)
            disposable.Dispose();
    }

    private async Task<IEmbeddingGenerator<string, Embedding<float>>> CreateGeneratorAsync()
    {
        Directory.CreateDirectory(_options.CacheDirectory ?? AppContext.BaseDirectory);
        _logger?.LogInformation(
            "Initializing local ONNX embedding generator. model={Model} modelPath={ModelPath} cache={CacheDirectory}",
            _options.EmbeddingModel,
            _options.ModelPath,
            _options.CacheDirectory);

        var localOptions = new LocalEmbeddingsOptions
        {
            ModelName = _options.EmbeddingModel,
            ModelPath = _options.ModelPath,
            CacheDirectory = _options.CacheDirectory,
            MaxSequenceLength = _options.MaxSequenceLength,
            NormalizeEmbeddings = _options.NormalizeEmbeddings,
            PreferQuantized = _options.PreferQuantized,
            EnsureModelDownloaded = _options.EnsureModelDownloaded
        };

        return await LocalEmbeddingGenerator.CreateAsync(localOptions).ConfigureAwait(false);
    }
}
