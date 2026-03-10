namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

/// <summary>
/// Options controlling Microsoft Agent Framework interop behavior.
/// </summary>
public sealed class MicrosoftAgentFrameworkInteropOptions
{
    /// <summary>Tool name exposed to OpenClaw.</summary>
    public string ToolName { get; set; } = "microsoft_agent_framework";

    /// <summary>
    /// Optional allowlist of MAF agent names. Empty => allow all.
    /// </summary>
    public string[] AllowedAgents { get; set; } = [];

    /// <summary>Default response format when "format" is not provided.</summary>
    public string DefaultResponseFormat { get; set; } = "text";

    /// <summary>Maximum accepted input length to bound payload size.</summary>
    public int MaxInputLength { get; set; } = 16_384;
}
