namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

/// <summary>
/// Normalized tool request forwarded to the host MAF runner.
/// </summary>
public sealed class MicrosoftAgentFrameworkToolRequest
{
    public required string Agent { get; init; }
    public required string Input { get; init; }
    public string? ThreadId { get; init; }
    public string? ContextJson { get; init; }
}
