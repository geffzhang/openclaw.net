namespace OpenClaw.Evaluation.Models;

/// <summary>
/// Wraps the outcome of an evaluation pass, including extracted tool usage
/// and optional quality scores. Serves as the entry point for fluent assertions.
/// </summary>
public sealed class EvaluationResult
{
    /// <summary>The underlying execution trace from which this result was derived.</summary>
    public AgentExecutionTrace? Trace { get; init; }

    /// <summary>Timestamp when the evaluation was performed.</summary>
    public DateTimeOffset EvaluatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Optional faithfulness score (0.0–1.0) from a RAG quality evaluator.</summary>
    public double? FaithfulnessScore { get; init; }

    /// <summary>Optional context relevance score (0.0–1.0).</summary>
    public double? ContextRelevanceScore { get; init; }

    /// <summary>Optional fluency score (0.0–1.0) from NLP-level evaluation.</summary>
    public double? FluencyScore { get; init; }

    /// <summary>Convenience: total tool invocations extracted from the trace.</summary>
    public int ToolCallCount => Trace?.ToolInvocations.Count ?? 0;

    /// <summary>Convenience: number of failed tool invocations.</summary>
    public int FailedToolCount => Trace?.FailedToolCount ?? 0;

    /// <summary>Whether all quality scores that are present meet the given minimum threshold.</summary>
    public bool MeetsQualityGate(double minimumScore)
    {
        if (FaithfulnessScore.HasValue && FaithfulnessScore.Value < minimumScore) return false;
        if (ContextRelevanceScore.HasValue && ContextRelevanceScore.Value < minimumScore) return false;
        if (FluencyScore.HasValue && FluencyScore.Value < minimumScore) return false;
        return true;
    }
}
