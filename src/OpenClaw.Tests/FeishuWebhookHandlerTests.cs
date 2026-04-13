using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;
using OpenClaw.Gateway;
using Xunit;

namespace OpenClaw.Tests;

public sealed class FeishuWebhookHandlerTests
{
    [Fact]
    public async Task HandleAsync_UrlVerification_ReturnsChallengeJson()
    {
        using var harness = new FeishuHandlerHarness(new FeishuChannelConfig
        {
            VerificationTokenRef = "",
            VerificationToken = "token",
            ValidateSignature = false
        });

        var result = await harness.Handler.HandleAsync(
            """{"type":"url_verification","token":"token","challenge":"abc123"}""",
            null,
            null,
            null,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        var payload = JsonDocument.Parse(result.Body!).RootElement;
        Assert.Equal("abc123", payload.GetProperty("challenge").GetString());
    }

    [Fact]
    public async Task HandleAsync_InvalidSignature_ReturnsUnauthorized()
    {
        using var harness = new FeishuHandlerHarness(new FeishuChannelConfig
        {
            VerificationTokenRef = "",
            VerificationToken = "token",
            ValidateSignature = true
        });

        var result = await harness.Handler.HandleAsync(
            """{"type":"event_callback","token":"token"}""",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            "nonce",
            "bad-signature",
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task HandleAsync_EncryptPayload_ReturnsExplicitError()
    {
        using var harness = new FeishuHandlerHarness(new FeishuChannelConfig
        {
            VerificationTokenRef = "",
            VerificationToken = "token",
            EncryptKeyRef = "",
            EncryptKey = "encrypt",
            ValidateSignature = false
        });

        var result = await harness.Handler.HandleAsync(
            """{"type":"event_callback","token":"token","encrypt":"ciphertext"}""",
            null,
            null,
            null,
            (_, _) => ValueTask.CompletedTask,
            CancellationToken.None);

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("Encrypt Key", result.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleAsync_GroupMentionedText_EnqueuesInboundMessage()
    {
        using var harness = new FeishuHandlerHarness(new FeishuChannelConfig
        {
            VerificationTokenRef = "",
            VerificationToken = "token",
            BotOpenIdRef = "",
            BotOpenId = "ou_bot",
            ValidateSignature = false,
            RequireMention = true,
            AllowedFromUserIds = ["ou_user"]
        });

        InboundMessage? captured = null;
        var body =
            """
            {
              "type": "event_callback",
              "token": "token",
              "header": {
                "event_id": "evt-1",
                "event_type": "im.message.receive_v1"
              },
              "event": {
                "sender": {
                  "sender_id": {
                    "open_id": "ou_user"
                  },
                  "sender_type": "user"
                },
                "message": {
                  "message_id": "om_1",
                  "chat_id": "oc_group_1",
                  "chat_type": "group",
                  "message_type": "text",
                  "content": "{\"text\":\"@OpenClaw hello feishu\"}",
                  "mentions": [
                    {
                      "id": {
                        "open_id": "ou_bot"
                      },
                      "name": "OpenClaw"
                    }
                  ]
                }
              }
            }
            """;

        var result = await harness.Handler.HandleAsync(
            body,
            null,
            null,
            null,
            (message, _) =>
            {
                captured = message;
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(captured);
        Assert.Equal("feishu", captured!.ChannelId);
        Assert.Equal("ou_user", captured.SenderId);
        Assert.Equal("hello feishu", captured.Text);
        Assert.True(captured.IsGroup);
        Assert.Equal("oc_group_1", captured.GroupId);
    }

    private sealed class FeishuHandlerHarness : IDisposable
    {
        private readonly string _storagePath = Path.Combine(Path.GetTempPath(), $"openclaw-feishu-{Guid.NewGuid():N}");

        public FeishuHandlerHarness(FeishuChannelConfig config)
        {
            Directory.CreateDirectory(_storagePath);
            Handler = new FeishuWebhookHandler(
                config,
                new AllowlistManager(_storagePath, NullLogger<AllowlistManager>.Instance),
                new RecentSendersStore(_storagePath, NullLogger<RecentSendersStore>.Instance),
                AllowlistSemantics.Legacy,
                NullLogger<FeishuWebhookHandler>.Instance);
        }

        public FeishuWebhookHandler Handler { get; }

        public void Dispose()
        {
            if (Directory.Exists(_storagePath))
                Directory.Delete(_storagePath, recursive: true);
        }
    }
}
