namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

/// <summary>
/// Normalized tool result returned by the host MAF runner.
/// </summary>
public sealed class MicrosoftAgentFrameworkToolResult
{
    public string Text { get; init; } = "";
    public string? ThreadId { get; init; }
    public string? StateJson { get; init; }
}
