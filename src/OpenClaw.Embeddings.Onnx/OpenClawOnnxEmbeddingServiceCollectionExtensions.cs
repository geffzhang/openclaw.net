using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Embeddings.Onnx;

public static class OpenClawOnnxEmbeddingServiceCollectionExtensions
{
    public static IServiceCollection AddOpenClawOnnxEmbeddings(
        this IServiceCollection services,
        ToolSemanticRoutingConfig config,
        string gatewayDataPath)
    {
        var options = new OpenClawOnnxEmbeddingOptions
        {
            EmbeddingModel = string.IsNullOrWhiteSpace(config.EmbeddingModel)
                ? "sentence-transformers/all-MiniLM-L6-v2"
                : config.EmbeddingModel.Trim(),
            ModelPath = ResolvePath(config.ModelPath),
            CacheDirectory = ResolveCacheDirectory(config.CacheDirectory, gatewayDataPath),
            MaxSequenceLength = Math.Max(1, config.MaxSequenceLength),
            NormalizeEmbeddings = config.NormalizeEmbeddings,
            PreferQuantized = config.PreferQuantized,
            EnsureModelDownloaded = config.EnsureModelDownloaded
        };

        services.AddSingleton(options);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, OpenClawOnnxEmbeddingGenerator>();
        return services;
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.GetFullPath(expanded);
    }

    private static string ResolveCacheDirectory(string? configuredCacheDirectory, string gatewayDataPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredCacheDirectory))
            return ResolvePath(configuredCacheDirectory)!;

        var root = string.IsNullOrWhiteSpace(gatewayDataPath)
            ? Path.Combine(AppContext.BaseDirectory, "data")
            : gatewayDataPath;
        return Path.GetFullPath(Path.Combine(root, "cache", "embeddings", "onnx"));
    }
}
