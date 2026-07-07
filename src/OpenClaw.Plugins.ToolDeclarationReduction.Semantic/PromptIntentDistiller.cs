namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

internal static class PromptIntentDistiller
{
    private static readonly string[] CoordinationSeparators = [" and ", " then ", " also ", " plus "];
    private static readonly string[] SchemaHints = ["path", "url", "query", "chatid", "command", "file", "patch", "message", "text"];

    public static IReadOnlyList<string> DistillActionPhrases(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var phrases = prompt
            .Split(['.', ';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(static part => part.Split(CoordinationSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(static part => part.Trim())
            .Where(static part => part.Length >= 3)
            .Select(static part => part.Length > 96 ? part[..96] : part)
            .Where(ContainsUsefulTerms)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return phrases.Length > 0 ? phrases : [prompt.Trim()];
    }

    private static bool ContainsUsefulTerms(string phrase)
    {
        if (phrase.Any(static ch => ch == '_' || char.IsDigit(ch)))
            return true;

        foreach (var hint in SchemaHints)
        {
            if (phrase.Contains(hint, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length >= 2;
    }
}