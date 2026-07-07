namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

internal static class CosineSimilarity
{
    public static double Score(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var score = 0.0;
        for (var index = 0; index < count; index++)
            score += left[index] * right[index];

        return Math.Clamp(score, 0.0, 1.0);
    }
}