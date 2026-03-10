using System.Diagnostics;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Observability;

namespace OpenClaw.MicrosoftAgentFrameworkAdapter;

/// <summary>
/// A single OpenClaw tool that can invoke any MAF agent by name.
/// </summary>
public sealed class MicrosoftAgentFrameworkEntrypointTool : ITool
{
    private readonly IMicrosoftAgentFrameworkRunner _runner;
    private readonly MicrosoftAgentFrameworkInteropOptions _options;

    public MicrosoftAgentFrameworkEntrypointTool(
        IMicrosoftAgentFrameworkRunner runner,
        MicrosoftAgentFrameworkInteropOptions? options = null)
    {
        _runner = runner;
        _options = options ?? new MicrosoftAgentFrameworkInteropOptions();
    }

    public string Name => _options.ToolName;

    public string Description =>
        "Invoke a Microsoft Agent Framework ChatAgent by agent name while keeping OpenClaw policy boundaries.";

    public string ParameterSchema => """
        {
          "type": "object",
          "properties": {
            "agent": { "type": "string", "description": "MAF ChatAgent name" },
            "input": { "type": "string", "description": "User input to the agent" },
            "thread_id": { "type": "string", "description": "Optional conversation thread identifier" },
            "context": { "type": "object", "description": "Optional context object passed to the runner", "default": {} },
            "format": { "type": "string", "enum": ["text","json"], "default": "text" }
          },
          "required": ["agent","input"],
          "additionalProperties": false
        }
        """;

    public async ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
    {
        using var activity = Telemetry.ActivitySource.StartActivity("Tool.MicrosoftAgentFramework.Invoke");
        activity?.SetTag("maf.tool_name", Name);

        var agent = "";
        var format = _options.DefaultResponseFormat;
        string? threadId = null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("agent", out var agentEl) || agentEl.ValueKind != JsonValueKind.String)
                return "Error: 'agent' is required.";

            if (!root.TryGetProperty("input", out var inputEl) || inputEl.ValueKind != JsonValueKind.String)
                return "Error: 'input' is required.";

            agent = agentEl.GetString() ?? "";
            var input = inputEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(agent))
                return "Error: 'agent' is required.";
            if (string.IsNullOrWhiteSpace(input))
                return "Error: 'input' is required.";

            if (_options.MaxInputLength > 0 && input.Length > _options.MaxInputLength)
                return $"Error: 'input' exceeds max length {_options.MaxInputLength}.";

            if (_options.AllowedAgents.Length > 0 &&
                !_options.AllowedAgents.Contains(agent, StringComparer.OrdinalIgnoreCase))
            {
                return "Error: agent is not allowed.";
            }

            threadId = root.TryGetProperty("thread_id", out var threadEl) && threadEl.ValueKind == JsonValueKind.String
                ? threadEl.GetString()
                : null;

            format = root.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.String
                ? (formatEl.GetString() ?? _options.DefaultResponseFormat)
                : _options.DefaultResponseFormat;

            string? contextJson = null;
            if (root.TryGetProperty("context", out var contextEl) && contextEl.ValueKind != JsonValueKind.Null)
                contextJson = contextEl.GetRawText();

            activity?.SetTag("maf.agent", agent);
            activity?.SetTag("maf.thread_id", threadId);

            var result = await _runner.InvokeAsync(new MicrosoftAgentFrameworkToolRequest
            {
                Agent = agent,
                Input = input,
                ThreadId = threadId,
                ContextJson = contextJson
            }, ct);

            if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return result.Text;

            return JsonSerializer.Serialize(new
            {
                ok = true,
                agent,
                thread_id = result.ThreadId ?? threadId,
                text = result.Text,
                state_json = result.StateJson,
                error = (string?)null
            });
        }
        catch (Exception ex)
        {
            activity?.SetTag("error.type", ex.GetType().Name);
            activity?.SetTag("error.message", ex.Message);

            if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return $"Error: Microsoft Agent Framework invocation failed ({ex.GetType().Name}).";

            return JsonSerializer.Serialize(new
            {
                ok = false,
                agent,
                thread_id = threadId,
                text = (string?)null,
                state_json = (string?)null,
                error = ex.Message
            });
        }
    }
}
