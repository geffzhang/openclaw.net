using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using OpenClaw.Agent.Tools;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class A2UIInsForgeTests
{
    [Fact]
    public void A2UIInstruction_SerializesWithCamelCaseType()
    {
        using var value = JsonDocument.Parse("""{"count":1}""");
        var instruction = new A2UIInstruction
        {
            Type = "updateDataModel",
            Path = "/cart/count",
            Value = value.RootElement.Clone()
        };

        var json = JsonSerializer.Serialize(instruction, CoreJsonContext.Default.A2UIInstruction);

        Assert.Contains("\"type\":\"updateDataModel\"", json, StringComparison.Ordinal);
        Assert.Contains("\"path\":\"/cart/count\"", json, StringComparison.Ordinal);
        Assert.Contains("\"value\":{\"count\":1}", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/cart/items/0")]
    [InlineData("/escaped/~0/~1")]
    public void JsonPointerValidation_AllowsSafePointers(string path)
    {
        Assert.True(A2UIProtocol.IsSafeJsonPointer(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("cart/items")]
    [InlineData("/bad/~2")]
    public void JsonPointerValidation_RejectsUnsafePointers(string path)
    {
        Assert.False(A2UIProtocol.IsSafeJsonPointer(path));
    }

    [Fact]
    public async Task A2UIChannel_ConvertsClientEventToInboundMessage()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig { MaxMessageBytes = 1024 });
        var ws = new TestWebSocket();
        ws.QueueReceiveText("""{"type":"submit","actionId":"claim-form","sessionId":"s1","data":{"approved":true}}""");
        ws.QueueClose();

        InboundMessage? received = null;
        channel.OnMessageReceived += (msg, _) =>
        {
            received = msg;
            return ValueTask.CompletedTask;
        };

        await channel.HandleConnectionAsync(ws, "client-1", IPAddress.Loopback, CancellationToken.None);

        Assert.NotNull(received);
        Assert.Equal("a2ui", received!.ChannelId);
        Assert.Equal("client-1", received.SenderId);
        Assert.Equal("s1", received.SessionId);
        Assert.Contains("claim-form", received.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A2UIChannel_SendsJsonLineInstruction()
    {
        var channel = new A2UIChannel(new A2UIChannelConfig());
        var ws = new TestWebSocket();
        Assert.True(channel.TryAddConnectionForTest("client-1", ws, IPAddress.Loopback));

        using var value = JsonDocument.Parse("""{"items":[]}""");
        await channel.SendInstructionAsync(
            "client-1",
            new A2UIInstruction
            {
                Type = "updateDataModel",
                Path = "/cart",
                Value = value.RootElement.Clone()
            },
            CancellationToken.None);

        var payload = Encoding.UTF8.GetString(ws.Sent.Single());
        Assert.EndsWith('\n', payload);
        var instruction = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.A2UIInstruction);
        Assert.Equal("updateDataModel", instruction!.Type);
        Assert.Equal("/cart", instruction.Path);
    }

    [Fact]
    public async Task InsForgeQueryComponentTool_PostsToConfiguredEndpoint()
    {
        var handler = new CapturingHandler("""{"ok":true}""");
        var http = new HttpClient(handler);
        var tool = new InsForgeQueryComponentTool(
            new InsForgeConfig
            {
                Enabled = true,
                Endpoint = "https://insforge.test",
                ApiKeyRef = "raw:test-key",
                ComponentQueryPath = "/functions/v1/query"
            },
            http);

        var result = await tool.ExecuteAsync("""{"query":"insurance claim form","limit":1}""", CancellationToken.None);

        Assert.Equal("""{"ok":true}""", result);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://insforge.test/functions/v1/query", handler.Request.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task InsForgeUpdateDataModelTool_RejectsUnsafePath()
    {
        var tool = new InsForgeUpdateDataModelTool(
            new InsForgeConfig { Enabled = true, Endpoint = "https://insforge.test" },
            new HttpClient(new CapturingHandler("{}")));

        var result = await tool.ExecuteAsync("""{"session_id":"s1","path":"cart","value":1}""", CancellationToken.None);

        Assert.Contains("safe RFC 6901", result, StringComparison.Ordinal);
    }

    [Fact]
    public void InsForgeRealtimeBridge_MapsRecordToA2UIUpdate()
    {
        var config = new InsForgeConfig();
        var ok = InsForgeRealtimeBridge.TryBuildUpdate(
            """{"record":{"sessionId":"s1","recipientId":"client-1","path":"/cart/items/0","value":{"price":12}}}""",
            config,
            out var recipientId,
            out var instruction,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("client-1", recipientId);
        Assert.Equal("updateDataModel", instruction.Type);
        Assert.Equal("/cart/items/0", instruction.Path);
        Assert.Equal(12, instruction.Value!.Value.GetProperty("price").GetInt32());
    }

    [Fact]
    public void InsForgeAndA2UI_AreDefaultOff()
    {
        var config = new GatewayConfig();

        Assert.False(config.InsForge.Enabled);
        Assert.False(config.Channels.A2UI.Enabled);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _response;

        public CapturingHandler(string response)
        {
            _response = response;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            });
        }
    }
}
