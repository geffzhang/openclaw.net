namespace OpenClaw.Embeddings.Onnx;

public sealed class OpenClawOnnxEmbeddingOptions
{
    public string EmbeddingModel { get; set; } = "sentence-transformers/all-MiniLM-L6-v2";
    public string? ModelPath { get; set; }
    public string? CacheDirectory { get; set; }
    public int MaxSequenceLength { get; set; } = 512;
    public bool NormalizeEmbeddings { get; set; }
    public bool PreferQuantized { get; set; }
    public bool EnsureModelDownloaded { get; set; } = true;
}
