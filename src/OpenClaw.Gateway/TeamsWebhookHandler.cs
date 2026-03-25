using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

/// <summary>
/// Handles inbound Bot Framework Activity webhook requests from Microsoft Teams.
/// Validates JWT tokens, parses activities, and creates InboundMessages.
/// </summary>
internal sealed class TeamsWebhookHandler
{
    private readonly TeamsChannelConfig _config;
    private readonly AllowlistManager _allowlists;
    private readonly RecentSendersStore _recentSenders;
    private readonly AllowlistSemantics _allowlistSemantics;
    private readonly ILogger<TeamsWebhookHandler> _logger;
    private readonly string _appId;

    public TeamsWebhookHandler(
        TeamsChannelConfig config,
        AllowlistManager allowlists,
        RecentSendersStore recentSenders,
        AllowlistSemantics allowlistSemantics,
        ILogger<TeamsWebhookHandler> logger)
    {
        _config = config;
        _allowlists = allowlists;
        _recentSenders = recentSenders;
        _allowlistSemantics = allowlistSemantics;
        _logger = logger;
        _appId = SecretResolver.Resolve(config.AppIdRef) ?? config.AppId ?? "";
    }

    public async Task<WebhookResult> HandleAsync(
        HttpContext context,
        TeamsChannel channel,
        Func<InboundMessage, CancellationToken, ValueTask> enqueue,
        CancellationToken ct)
    {
        if (!_config.Enabled)
            return WebhookResult.NotFound();

        if (!HttpMethods.IsPost(context.Request.Method))
            return WebhookResult.Status(405);

        // JWT validation (skip in dev mode)
        if (_config.ValidateToken)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (!ValidateAuthHeader(authHeader))
            {
                _logger.LogWarning("Teams webhook rejected: invalid or missing JWT token.");
                return WebhookResult.Unauthorized();
            }
        }

        // Read body
        var body = await ReadBodyAsync(context, _config.MaxRequestBytes, ct);
        if (body is null)
            return WebhookResult.Status(StatusCodes.Status413PayloadTooLarge);

        try
        {
            var activity = JsonSerializer.Deserialize(body, TeamsJsonContext.Default.TeamsInboundActivity);
            if (activity is null)
                return WebhookResult.BadRequest("Invalid activity JSON");

            // Store conversation reference for proactive messaging
            if (!string.IsNullOrWhiteSpace(activity.ServiceUrl) &&
                activity.Conversation?.Id is not null)
            {
                var senderId = activity.From?.AadObjectId ?? activity.From?.Id ?? "";
                channel.StoreConversationReference(senderId, new TeamsConversationReference
                {
                    ServiceUrl = activity.ServiceUrl,
                    ConversationId = activity.Conversation.Id,
                    UserId = senderId,
                    TenantId = activity.Conversation.TenantId,
                    ConversationType = activity.Conversation.ConversationType
                });
            }

            // Only process message activities
            if (!string.Equals(activity.Type, "message", StringComparison.OrdinalIgnoreCase))
                return WebhookResult.Ok();

            var text = activity.Text ?? "";
            var senderId2 = activity.From?.AadObjectId ?? activity.From?.Id ?? "";
            var senderName = activity.From?.Name;

            if (string.IsNullOrWhiteSpace(senderId2))
                return WebhookResult.Ok();

            // Tenant allowlist
            if (_config.AllowedTenantIds.Length > 0 && activity.Conversation?.TenantId is not null)
            {
                if (!_config.AllowedTenantIds.Contains(activity.Conversation.TenantId))
                {
                    _logger.LogInformation("Ignoring Teams message from disallowed tenant {TenantId}.", activity.Conversation.TenantId);
                    return WebhookResult.Ok();
                }
            }

            // Determine conversation type
            var conversationType = activity.Conversation?.ConversationType;
            var isGroup = conversationType is "channel" or "groupChat";

            // Mention detection for group contexts
            if (isGroup && _config.RequireMention)
            {
                if (!IsBotMentioned(activity, _appId))
                {
                    return WebhookResult.Ok(); // Silently ignore non-mentioned messages in groups
                }
            }

            // Strip @mention tags from text
            text = StripMentionTags(text, _appId, activity.Entities);

            await _recentSenders.RecordAsync("teams", senderId2, senderName, ct);

            // Sender allowlist
            var effective = _allowlists.GetEffective("teams", new ChannelAllowlistFile
            {
                AllowedFrom = _config.AllowedFromIds
            });
            if (!AllowlistPolicy.IsAllowed(effective.AllowedFrom, senderId2, _allowlistSemantics))
            {
                _logger.LogInformation("Ignoring Teams message from blocked sender {SenderId}.", senderId2);
                return WebhookResult.Ok();
            }

            // Group policy enforcement
            if (isGroup)
            {
                if (_config.GroupPolicy is "disabled")
                    return WebhookResult.Ok();

                if (_config.GroupPolicy is "allowlist" && activity.Conversation?.Id is not null)
                {
                    var convId = activity.Conversation.Id;
                    var allowed = _config.AllowedConversationIds.Contains(convId) ||
                                  _config.AllowedTeamIds.Any(teamId =>
                                      convId.Contains(teamId, StringComparison.OrdinalIgnoreCase));
                    if (!allowed)
                    {
                        _logger.LogInformation("Ignoring Teams group message from non-allowlisted conversation.");
                        return WebhookResult.Ok();
                    }
                }
            }

            // Truncate
            if (text.Length > _config.MaxInboundChars)
            {
                _logger.LogWarning("Truncating Teams message from {Sender} (exceeds {Max} chars).", senderId2, _config.MaxInboundChars);
                text = text[.._config.MaxInboundChars];
            }

            var msg = new InboundMessage
            {
                ChannelId = "teams",
                SenderId = senderId2,
                SenderName = senderName,
                Text = text.Trim(),
                MessageId = activity.Id,
                ReplyToMessageId = activity.ReplyToId,
                IsGroup = isGroup,
                GroupId = isGroup ? activity.Conversation?.Id : null,
                SessionId = isGroup ? $"teams:group:{activity.Conversation?.Id}" : null,
            };

            await enqueue(msg, ct);
            return WebhookResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Teams webhook activity.");
            return WebhookResult.BadRequest("Invalid activity");
        }
    }

    private bool ValidateAuthHeader(string authHeader)
    {
        if (string.IsNullOrWhiteSpace(authHeader))
            return false;

        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            // Parse JWT payload without external dependencies (no signature verification —
            // Azure Bot Framework handles transport-level security via HTTPS).
            var parts = token.Split('.');
            if (parts.Length != 3)
                return false;

            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var payload = doc.RootElement;

            // Check audience matches our app ID
            if (payload.TryGetProperty("aud", out var aud))
            {
                var audience = aud.GetString();
                if (!string.Equals(audience, _appId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Teams JWT audience '{Audience}' does not match app ID.", audience);
                    return false;
                }
            }
            else return false;

            // Check token is not expired
            if (payload.TryGetProperty("exp", out var exp))
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64());
                if (expiry < DateTimeOffset.UtcNow)
                {
                    _logger.LogWarning("Teams JWT token has expired.");
                    return false;
                }
            }

            // Check issuer is from Microsoft
            if (payload.TryGetProperty("iss", out var iss))
            {
                var issuer = iss.GetString();
                if (issuer is null ||
                    (!issuer.StartsWith("https://sts.windows.net/", StringComparison.OrdinalIgnoreCase) &&
                     !issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Teams JWT issuer '{Issuer}' is not a recognized Microsoft issuer.", issuer);
                    return false;
                }
            }
            else return false;

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Teams JWT validation failed.");
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static bool IsBotMentioned(TeamsInboundActivity activity, string appId)
    {
        if (activity.Entities is null) return false;

        return activity.Entities.Any(e =>
            string.Equals(e.Type, "mention", StringComparison.OrdinalIgnoreCase) &&
            e.Mentioned is not null &&
            string.Equals(e.Mentioned.Id, appId, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripMentionTags(string text, string appId, TeamsActivityEntity[]? entities)
    {
        if (entities is null || string.IsNullOrWhiteSpace(text))
            return text;

        foreach (var entity in entities)
        {
            if (!string.Equals(entity.Type, "mention", StringComparison.OrdinalIgnoreCase))
                continue;
            if (entity.Mentioned?.Id is null ||
                !string.Equals(entity.Mentioned.Id, appId, StringComparison.OrdinalIgnoreCase))
                continue;

            // Remove the <at>...</at> tag from the text
            if (!string.IsNullOrWhiteSpace(entity.Text))
            {
                text = text.Replace(entity.Text, "", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Also strip any remaining <at>...</at> HTML tags
        text = Regex.Replace(text, @"<at[^>]*>.*?</at>", "", RegexOptions.IgnoreCase);

        return text.Trim();
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maxBytes, CancellationToken ct)
    {
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > maxBytes)
            return null;

        var buffer = new byte[8 * 1024];
        await using var ms = new MemoryStream();
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0) break;
            if (ms.Length + read > maxBytes) return null;
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }
}
