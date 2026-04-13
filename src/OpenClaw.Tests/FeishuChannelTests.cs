using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FeishuChannelTests
{
    [Fact]
    public async Task SendAsync_MarkdownPayload_UsesInteractiveCard()
    {
        string? authPayload = null;
        string? sendPayload = null;

        using var http = new HttpClient(new CallbackHandler(request =>
        {
            if (request.RequestUri?.AbsoluteUri.Contains("/tenant_access_token/internal", StringComparison.Ordinal) == true)
            {
                authPayload = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"code":0,"tenant_access_token":"tenant-token","expire":7200}""")
                };
            }

            sendPayload = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"code":0,"data":{"message_id":"msg-1"}}""")
            };
        }));

        await using var channel = new FeishuChannel(
            new FeishuChannelConfig
            {
                Enabled = true,
                AppIdRef = "raw:app-id",
                AppSecretRef = "raw:app-secret"
            },
            NullLogger<FeishuChannel>.Instance,
            http);

        await channel.SendAsync(new OutboundMessage
        {
            ChannelId = "feishu",
            RecipientId = "oc_chat_1",
            Text = "```bash\necho hi\n```"
        }, CancellationToken.None);

        Assert.NotNull(authPayload);
        Assert.NotNull(sendPayload);

        var auth = JsonDocument.Parse(authPayload!).RootElement;
        Assert.Equal("app-id", auth.GetProperty("app_id").GetString());

        var send = JsonDocument.Parse(sendPayload!).RootElement;
        Assert.Equal("oc_chat_1", send.GetProperty("receive_id").GetString());
        Assert.Equal("interactive", send.GetProperty("msg_type").GetString());
    }

    private sealed class CallbackHandler(Func<HttpRequestMessage, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(callback(request));
        }
    }
}
