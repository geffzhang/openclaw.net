namespace OpenClaw.Evaluation.Assertions;

/// <summary>
/// Exception thrown when one or more evaluation assertions fail.
/// Integrates with xUnit's exception-based test failure model.
/// </summary>
public sealed class EvaluationAssertionException : Exception
{
    public EvaluationAssertionException(string message) : base(message) { }
    public EvaluationAssertionException(string message, Exception innerException) : base(message, innerException) { }
}
