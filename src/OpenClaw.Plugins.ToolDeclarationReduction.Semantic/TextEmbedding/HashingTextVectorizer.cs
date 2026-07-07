namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

internal sealed class HashingTextVectorizer
{
    public const int DefaultDimensions = 512;

    public float[] Vectorize(string text, int dimensions = DefaultDimensions)
    {
        var vector = new float[dimensions];
        foreach (var token in Tokenize(text))
        {
            var bucket = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(token)) % dimensions;
            vector[bucket] += 1f;
        }

        Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var word in ReadWords(text))
        {
            yield return word;

            foreach (var part in word.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.Equals(part, word, StringComparison.OrdinalIgnoreCase))
                    yield return part;
            }

            if (word.Length >= 4)
            {
                for (var index = 0; index <= word.Length - 3; index++)
                    yield return "ng:" + word.Substring(index, 3);
            }
        }
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        foreach (var value in vector)
            sum += value * value;

        if (sum <= 0)
            return;

        var length = Math.Sqrt(sum);
        for (var index = 0; index < vector.Length; index++)
            vector[index] = (float)(vector[index] / length);
    }

    private static IEnumerable<string> ReadWords(string text)
    {
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isWord = index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_');
            if (isWord && start < 0)
            {
                start = index;
            }
            else if (!isWord && start >= 0)
            {
                yield return text[start..index].ToLowerInvariant();
                start = -1;
            }
        }
    }
}