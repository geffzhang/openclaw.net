using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Agent.Integrations;

/// <summary>
/// Wraps an OpenClaw <see cref="ITool"/> as an <see cref="AIFunction"/> so that
/// Microsoft Agent Framework's <c>ChatClientAgent</c> can invoke it via the
/// <c>FunctionInvokingChatClient</c> middleware.
/// </summary>
internal sealed class ToolAIFunction : AIFunction
{
    private readonly ITool _tool;

    public ToolAIFunction(ITool tool)
    {
        _tool = tool;

        using var doc = JsonDocument.Parse(tool.ParameterSchema);
        JsonSchema = doc.RootElement.Clone();
    }

    public override string Name => _tool.Name;

    public override string Description => _tool.Description;

    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        // Serialize the structured arguments back to JSON for the OpenClaw tool interface.
        var dict = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);
        foreach (var kvp in arguments)
            dict[kvp.Key] = kvp.Value;

        var argsJson = JsonSerializer.Serialize(dict, CoreJsonContext.Default.IDictionaryStringObject);
        return await _tool.ExecuteAsync(argsJson, cancellationToken);
    }
}
