using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class FeishuWebhookHandler
{
    private const string UnsupportedEncryptMessage = "你配置了Encrypt Key，请关闭该功能。";

    private readonly FeishuChannelConfig _config;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _semantics;
    private readonly ILogger<FeishuWebhookHandler> _logger;
    private readonly string? _verificationToken;
    private readonly string? _encryptKey;
    private readonly string? _botOpenId;

    public FeishuWebhookHandler(
        FeishuChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics semantics,
        ILogger<FeishuWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _semantics = semantics;
        _logger = logger;
        _verificationToken = SecretResolver.Resolve(config.VerificationTokenRef) ?? config.VerificationToken;
        _encryptKey = SecretResolver.Resolve(config.EncryptKeyRef) ?? config.EncryptKey;
        _botOpenId = SecretResolver.Resolve(config.BotOpenIdRef) ?? config.BotOpenId;
    }

    public async ValueTask<WebhookResult> HandleAsync(
        string bodyText,
        string? timestampHeader,
        string? nonceHeader,
        string? signatureHeader,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        if (_config.ValidateSignature && !ValidateSignature(bodyText, timestampHeader, nonceHeader, signatureHeader))
        {
            _logger.LogWarning("Rejected Feishu webhook due to invalid signature.");
            return WebhookResult.Unauthorized();
        }

        var envelope = JsonSerializer.Deserialize(bodyText, FeishuJsonContext.Default.FeishuWebhookEnvelope);
        if (envelope is null)
            return WebhookResult.BadRequest("Invalid Feishu payload.");

        if (!ValidateToken(envelope.Token ?? envelope.Header?.Token))
        {
            _logger.LogWarning("Rejected Feishu webhook due to invalid verification token.");
            return WebhookResult.Unauthorized();
        }

        if (!string.IsNullOrWhiteSpace(envelope.Encrypt))
        {
            if (!string.IsNullOrWhiteSpace(_encryptKey))
                _logger.LogWarning("Feishu encrypt payload received but decrypt flow is not enabled.");
            return WebhookResult.BadRequest(UnsupportedEncryptMessage);
        }

        if (string.Equals(envelope.Type, "url_verification", StringComparison.Ordinal))
        {
            var payload = JsonSerializer.Serialize(
                new FeishuUrlVerificationResponse { Challenge = envelope.Challenge },
                FeishuJsonContext.Default.FeishuUrlVerificationResponse);
            return new WebhookResult(StatusCodes.Status200OK, "application/json; charset=utf-8", payload);
        }

        if (!string.Equals(envelope.Type, "event_callback", StringComparison.Ordinal))
            return WebhookResult.Ok();
        if (!string.Equals(envelope.Header?.EventType, "im.message.receive_v1", StringComparison.Ordinal))
            return WebhookResult.Ok();

        var message = envelope.Event?.Message;
        var sender = envelope.Event?.Sender;
        var senderId = sender?.SenderId?.OpenId;
        if (message is null || string.IsNullOrWhiteSpace(senderId))
            return WebhookResult.Ok();
        if (!string.Equals(sender?.SenderType, "user", StringComparison.OrdinalIgnoreCase))
            return WebhookResult.Ok();

        if (_config.AllowedChatIds.Length > 0 &&
            (string.IsNullOrWhiteSpace(message.ChatId) ||
             !_config.AllowedChatIds.Contains(message.ChatId, StringComparer.Ordinal)))
        {
            return WebhookResult.Ok();
        }

        await _recentSenders.RecordAsync("feishu", senderId, senderName: null, ct);

        var effective = _allowlists.GetEffective("feishu", new ChannelAllowlistFile
        {
            AllowedFrom = _config.AllowedFromUserIds
        });
        if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderId, _semantics))
        {
            _logger.LogInformation("Ignoring Feishu message from blocked sender {SenderId}.", senderId);
            return WebhookResult.Ok();
        }

        var text = ExtractText(message);
        if (string.IsNullOrWhiteSpace(text))
            return WebhookResult.Ok();

        var isDm = string.Equals(message.ChatType, "p2p", StringComparison.OrdinalIgnoreCase);
        if (!isDm && _config.RequireMention && !IsBotMentioned(message, text))
            return WebhookResult.Ok();

        text = StripMentions(message, text);
        if (text.Length > _config.MaxInboundChars)
            text = text[.._config.MaxInboundChars];
        if (string.IsNullOrWhiteSpace(text))
            return WebhookResult.Ok();

        var threadRoot = message.RootId ?? message.ParentId;
        await enqueue(new InboundMessage
        {
            ChannelId = "feishu",
            SenderId = senderId,
            Text = text.Trim(),
            MessageId = message.MessageId,
            ReplyToMessageId = message.ParentId ?? message.RootId,
            IsGroup = !isDm,
            GroupId = isDm ? null : message.ChatId,
            SessionId = !string.IsNullOrWhiteSpace(threadRoot) && !string.IsNullOrWhiteSpace(message.ChatId)
                ? $"feishu:thread:{message.ChatId}:{threadRoot}"
                : isDm ? null : $"feishu:{message.ChatId}:{senderId}"
        }, ct);

        return WebhookResult.Ok();
    }

    public static string ResolveDeliveryKey(string bodyText)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize(bodyText, FeishuJsonContext.Default.FeishuWebhookEnvelope);
            if (!string.IsNullOrWhiteSpace(envelope?.Header?.EventId))
                return envelope.Header.EventId;
            if (!string.IsNullOrWhiteSpace(envelope?.Event?.Message?.MessageId))
                return envelope.Event.Message.MessageId;
        }
        catch
        {
        }

        return WebhookDeliveryStore.HashDeliveryKey(bodyText);
    }

    public static bool IsUrlVerificationPayload(string bodyText)
        => bodyText.Contains("\"url_verification\"", StringComparison.Ordinal);

    private bool ValidateSignature(string bodyText, string? timestamp, string? nonce, string? signature)
    {
        if (string.IsNullOrWhiteSpace(_verificationToken) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            string.IsNullOrWhiteSpace(nonce) ||
            string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        if (!long.TryParse(timestamp, out var ts))
            return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
            return false;

        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(_verificationToken),
            Encoding.UTF8.GetBytes(timestamp + nonce + bodyText));
        var provided = NormalizeSignature(signature);
        return ConstantEquals(provided, Convert.ToHexStringLower(hash)) ||
               ConstantEquals(provided, Convert.ToBase64String(hash));
    }

    private bool ValidateToken(string? providedToken)
    {
        if (string.IsNullOrWhiteSpace(_verificationToken))
            return true;
        return string.Equals(providedToken, _verificationToken, StringComparison.Ordinal);
    }

    private bool IsBotMentioned(FeishuEventMessage message, string text)
    {
        if (message.Mentions is { Length: > 0 })
        {
            if (string.IsNullOrWhiteSpace(_botOpenId))
                return true;
            return message.Mentions.Any(item => string.Equals(item.Id?.OpenId, _botOpenId, StringComparison.Ordinal));
        }

        return !string.IsNullOrWhiteSpace(_botOpenId) &&
               text.Contains(_botOpenId, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripMentions(FeishuEventMessage message, string text)
    {
        if (message.Mentions is null)
            return text.Trim();

        foreach (var mention in message.Mentions)
        {
            if (!string.IsNullOrWhiteSpace(mention.Key))
                text = text.Replace(mention.Key, "", StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(mention.Name))
                text = text.Replace($"@{mention.Name}", "", StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }

    private static string? ExtractText(FeishuEventMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Content))
            return null;

        return message.MessageType switch
        {
            "text" => JsonSerializer.Deserialize(message.Content, FeishuJsonContext.Default.FeishuTextContent)?.Text,
            "image" => BuildImageMarker(JsonSerializer.Deserialize(message.Content, FeishuJsonContext.Default.FeishuImageContent)),
            "file" => BuildFileMarker(JsonSerializer.Deserialize(message.Content, FeishuJsonContext.Default.FeishuFileContent)),
            _ => null
        };
    }

    private static string? BuildImageMarker(FeishuImageContent? content)
        => string.IsNullOrWhiteSpace(content?.ImageKey) ? null : $"[IMAGE:feishu:image_key={content.ImageKey}]";

    private static string? BuildFileMarker(FeishuFileContent? content)
    {
        if (string.IsNullOrWhiteSpace(content?.FileKey))
            return null;

        return string.IsNullOrWhiteSpace(content.FileName)
            ? $"[FILE:feishu:file_key={content.FileKey}]"
            : $"[FILE:feishu:file_key={content.FileKey}] {content.FileName}";
    }

    private static string NormalizeSignature(string signature)
        => signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase) ? signature[7..] : signature;

    private static bool ConstantEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length &&
               CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
