namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

/// <summary>
/// Host-provided bridge to execute a Microsoft Agent Framework agent invocation.
/// </summary>
public interface IMicrosoftAgentFrameworkRunner
{
    ValueTask<MicrosoftAgentFrameworkToolResult> InvokeAsync(MicrosoftAgentFrameworkToolRequest request, CancellationToken ct);
}
