using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

/// <summary>
/// Plan step 5 — coverage for the new v0.9 A2UI tools.
/// </summary>
public sealed class A2UiV09ToolsTests
{
    [Fact]
    public async Task UpdateDataModel_MissingPathIsToolError()
    {
        var tool = new A2UiUpdateDataModelTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("""{"value":"x"}""", Context(), CancellationToken.None);

        Assert.Contains("'path' is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDataModel_RejectsUnsafeJsonPointer()
    {
        var tool = new A2UiUpdateDataModelTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"path":"no-leading-slash","value":1}""",
            Context(),
            CancellationToken.None);

        Assert.Contains("RFC 6901 JSON Pointer", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateDataModel_MissingValueIsToolError()
    {
        var tool = new A2UiUpdateDataModelTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("""{"path":"/users/0/name"}""", Context(), CancellationToken.None);

        Assert.Contains("'value' is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateComponents_MissingComponentsIsToolError()
    {
        var tool = new A2UiUpdateComponentsTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("{}", Context(), CancellationToken.None);

        Assert.Contains("'components' is required", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateComponents_RejectsDisallowedComponentType()
    {
        var tool = new A2UiUpdateComponentsTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"components":{"type":"evil-script","id":"a"}}""",
            Context(),
            CancellationToken.None);

        Assert.Contains("not allowed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateComponents_AcceptsCanonicalDictionaryType()
    {
        // Canonical types live in ComponentTypePolicy.DefaultComponentTypes; they should pass
        // the v0.9 tool's validation. (Past validation, the tool reaches the broker, which
        // returns "not connected in envelope mode" — that's the expected next-stage error and
        // confirms validation succeeded.)
        var tool = new A2UiUpdateComponentsTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync(
            """{"components":{"type":"text","id":"a","text":"hi"}}""",
            Context(),
            CancellationToken.None);

        Assert.DoesNotContain("not allowed", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSurface_RejectsNonWebSocketSession()
    {
        var tool = new A2UiCreateSurfaceTool(CreateBroker(), new GatewayConfig());

        var result = await tool.ExecuteAsync("{}", Context(channelId: "cli"), CancellationToken.None);

        Assert.Contains("websocket session", result, StringComparison.OrdinalIgnoreCase);
    }

    private static CanvasCommandBroker CreateBroker(GatewayConfig? config = null)
        => new(
            config ?? new GatewayConfig(),
            new WebSocketChannel(new WebSocketConfig()),
            new RuntimeEventStore(
                Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N")),
                NullLogger<RuntimeEventStore>.Instance));

    private static ToolExecutionContext Context(string channelId = "websocket")
        => new()
        {
            Session = new Session
            {
                Id = "sess",
                ChannelId = channelId,
                SenderId = "client"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess",
                ChannelId = channelId
            }
        };
}
