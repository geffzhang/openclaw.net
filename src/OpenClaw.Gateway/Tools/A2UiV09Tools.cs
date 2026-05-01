using System.Text.Json;
using OpenClaw.Core.A2UI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Canvas;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

/// <summary>
/// Plan step 5 — v0.9 A2UI tools. These accept v0.9-shaped payloads (surfaceId / path /
/// components), validate them through the unified <see cref="A2UIProtocol"/> + <see cref="ComponentTypePolicy"/>
/// layer, then emit a Canvas envelope of type <c>a2ui_instruction</c> that carries the v0.9
/// fields verbatim.
///
/// Capability gating: every v0.9 tool requires the connected Canvas client to advertise
/// <c>a2ui.v0_9</c> in its <c>canvas_ready</c> capabilities. Clients that have not yet been
/// upgraded continue to use the existing v0.8 <c>a2ui_push</c> path; they do not silently
/// regress — they receive a structured "v0.9 only" diagnostic, matching plan §五 risk mitigation.
/// </summary>
internal abstract class A2UiV09ToolBase : CanvasToolBase
{
    protected A2UiV09ToolBase(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }

    protected const string V09Capability = A2UICapabilities.V0_9;
    protected const string EnvelopeType = "a2ui_instruction";

    protected ComponentTypePolicy ComponentPolicy
        => ComponentTypePolicy.FromConfig(
            Config.Channels.A2UI.AllowedComponentTypes,
            Config.Channels.A2UI.AllowAnyComponentType);

    protected static JsonElement? CloneIfPresent(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind != JsonValueKind.Undefined)
            return prop.Clone();
        return null;
    }

    protected int MaxInstructionBytesEffective()
        => Math.Max(1, Math.Min(Config.Canvas.MaxCommandBytes, Config.Channels.A2UI.MaxInstructionBytes));
}

internal sealed class A2UiCreateSurfaceTool : A2UiV09ToolBase
{
    public A2UiCreateSurfaceTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_create_surface";
    public override string Description => "Create a new A2UI v0.9 surface on the connected Canvas client.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"}}}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        var surfaceId = SurfaceId(args.RootElement);

        return await SendAsync(argumentsJson, context, new WsServerEnvelope
        {
            Type = EnvelopeType,
            SurfaceId = surfaceId,
            InstructionType = "createSurface"
        }, "canvas_ack", V09Capability, ct);
    }
}

internal sealed class A2UiUpdateDataModelTool : A2UiV09ToolBase
{
    public A2UiUpdateDataModelTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_update_data_model";
    public override string Description => "Apply an A2UI v0.9 dataModel patch at the supplied JSON Pointer path.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"path":{"type":"string","description":"RFC 6901 JSON Pointer"},"value":{}},"required":["path","value"]}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        var root = args.RootElement;
        if (!TryGetRequiredString(root, "path", out var path, out var error))
            return error;

        if (!A2UIProtocol.IsSafeJsonPointer(path))
            return "Error: 'path' must be a safe RFC 6901 JSON Pointer (e.g. '/users/0/name').";

        var value = CloneIfPresent(root, "value");
        if (value is null)
            return "Error: 'value' is required.";

        var instruction = new WsServerEnvelope
        {
            Type = EnvelopeType,
            SurfaceId = SurfaceId(root),
            InstructionType = "updateDataModel",
            Path = path,
            Value = value
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(instruction, CoreJsonContext.Default.WsServerEnvelope);
        if (payloadBytes.Length > MaxInstructionBytesEffective())
            return $"Error: instruction exceeds {MaxInstructionBytesEffective()} bytes.";

        return await SendAsync(argumentsJson, context, instruction, "canvas_ack", V09Capability, ct);
    }
}

internal sealed class A2UiUpdateComponentsTool : A2UiV09ToolBase
{
    public A2UiUpdateComponentsTool(CanvasCommandBroker broker, GatewayConfig config) : base(broker, config) { }
    public override string Name => "a2ui_update_components";
    public override string Description => "Apply an A2UI v0.9 components subtree update on the connected Canvas client.";
    public override string ParameterSchema => """
        {"type":"object","properties":{"surfaceId":{"type":"string","default":"main"},"components":{"type":"object"}},"required":["components"]}
        """;

    public override async ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var args = ParseArgs(argumentsJson);
        var root = args.RootElement;
        var components = CloneIfPresent(root, "components");
        if (components is null)
            return "Error: 'components' is required.";

        if (!A2UIProtocol.ContainsOnlyAllowedComponents(components.Value, ComponentPolicy))
            return "Error: components subtree contains a component type that is not allowed.";

        var instruction = new WsServerEnvelope
        {
            Type = EnvelopeType,
            SurfaceId = SurfaceId(root),
            InstructionType = "updateComponents",
            Components = components
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(instruction, CoreJsonContext.Default.WsServerEnvelope);
        if (payloadBytes.Length > MaxInstructionBytesEffective())
            return $"Error: instruction exceeds {MaxInstructionBytesEffective()} bytes.";

        return await SendAsync(argumentsJson, context, instruction, "canvas_ack", V09Capability, ct);
    }
}
