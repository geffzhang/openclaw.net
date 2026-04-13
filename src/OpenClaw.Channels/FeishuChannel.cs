using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Http;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Channels;

public sealed partial class FeishuChannel : IChannelAdapter
{
    private readonly FeishuChannelConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<FeishuChannel> _logger;
    private readonly FeishuTenantAccessTokenManager _tokenManager;

    public FeishuChannel(FeishuChannelConfig config, ILogger<FeishuChannel> logger, HttpClient? http = null)
    {
        _config = config;
        _logger = logger;
        _http = http ?? HttpClientFactory.Create();

        var appId = SecretResolver.Resolve(config.AppIdRef) ?? config.AppId;
        var appSecret = SecretResolver.Resolve(config.AppSecretRef) ?? config.AppSecret;
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appSecret))
            throw new InvalidOperationException("Feishu app credentials are not configured.");

        _tokenManager = new FeishuTenantAccessTokenManager(_http, appId, appSecret, logger);
    }

    public string ChannelType => "feishu";
    public string ChannelId => "feishu";
#pragma warning disable CS0067 // Event is never used
    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async ValueTask SendAsync(OutboundMessage outbound, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(outbound.Text))
            return;

        var token = await _tokenManager.GetAccessTokenAsync(ct);
        if (BuildMessageBody(outbound.Text) is not { } body)
            return;

        using var request = CreateMessageRequest(outbound, body, token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(FeishuJsonContext.Default.FeishuSendMessageResponse, ct);
        if (payload is null)
            throw new InvalidOperationException("Feishu API returned an empty response.");
        if (payload.Code != 0)
            throw new InvalidOperationException(payload.Msg ?? $"Feishu API error {payload.Code}.");
    }

    private HttpRequestMessage CreateMessageRequest(OutboundMessage outbound, FeishuPreparedMessage body, string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            string.IsNullOrWhiteSpace(outbound.ReplyToMessageId)
                ? $"https://open.feishu.cn/open-apis/im/v1/messages?receive_id_type={ResolveReceiveIdType(outbound.RecipientId)}"
                : $"https://open.feishu.cn/open-apis/im/v1/messages/{Uri.EscapeDataString(outbound.ReplyToMessageId)}/reply");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (string.IsNullOrWhiteSpace(outbound.ReplyToMessageId))
        {
            request.Content = JsonContent.Create(
                new FeishuSendMessageRequest
                {
                    ReceiveId = outbound.RecipientId,
                    MsgType = body.MsgType,
                    Content = body.Content
                },
                FeishuJsonContext.Default.FeishuSendMessageRequest);
        }
        else
        {
            request.Content = JsonContent.Create(
                new FeishuReplyMessageRequest
                {
                    MsgType = body.MsgType,
                    Content = body.Content
                },
                FeishuJsonContext.Default.FeishuReplyMessageRequest);
        }

        return request;
    }

    private FeishuPreparedMessage? BuildMessageBody(string text)
    {
        var (markers, remaining) = MediaMarkerProtocol.Extract(text);
        var normalizedText = string.IsNullOrWhiteSpace(remaining) ? text : remaining;
        var fallback = markers
            .Select(static marker => $"[{marker.Kind}] {marker.Value}")
            .ToArray();

        var merged = string.IsNullOrWhiteSpace(normalizedText)
            ? string.Join('\n', fallback)
            : fallback.Length == 0 ? normalizedText : normalizedText + "\n" + string.Join('\n', fallback);
        if (string.IsNullOrWhiteSpace(merged))
            return null;

        var interactive = string.Equals(_config.RenderMode, "interactive", StringComparison.OrdinalIgnoreCase) ||
                          (string.Equals(_config.RenderMode, "auto", StringComparison.OrdinalIgnoreCase) && StructuredMarkdownRegex().IsMatch(merged));

        if (interactive)
        {
            var card = new FeishuInteractiveCard
            {
                Config = new FeishuInteractiveCardConfig { WideScreenMode = true },
                Elements =
                [
                    new FeishuInteractiveCardElement
                    {
                        Tag = "markdown",
                        Content = merged
                    }
                ]
            };

            return new FeishuPreparedMessage(
                "interactive",
                JsonSerializer.Serialize(card, FeishuJsonContext.Default.FeishuInteractiveCard));
        }

        return new FeishuPreparedMessage(
            "text",
            JsonSerializer.Serialize(new FeishuTextContent { Text = merged }, FeishuJsonContext.Default.FeishuTextContent));
    }

    private static string ResolveReceiveIdType(string recipientId)
        => recipientId.StartsWith("oc_", StringComparison.OrdinalIgnoreCase) ? "chat_id" : "open_id";

    [GeneratedRegex(@"(^|\n)(```|#{1,6}\s|>\s|\|.+\||[-*]\s)", RegexOptions.Multiline)]
    private static partial Regex StructuredMarkdownRegex();

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FeishuTenantAccessTokenManager(
    HttpClient http,
    string appId,
    string appSecret,
    ILogger logger)
{
    private const int MinimumTokenLifetimeSeconds = 60;
    private const int TokenRefreshBufferSeconds = 300;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt;

    public Exception? LastRefreshError { get; private set; }

    public async ValueTask<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(_token) && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(_token) && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            using var response = await http.PostAsJsonAsync(
                "https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal",
                new FeishuTenantAccessTokenRequest
                {
                    AppId = appId,
                    AppSecret = appSecret
                },
                FeishuJsonContext.Default.FeishuTenantAccessTokenRequest,
                ct);
            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync(FeishuJsonContext.Default.FeishuTenantAccessTokenResponse, ct)
                ?? throw new InvalidOperationException("Feishu tenant token response was empty.");
            if (payload.Code != 0 || string.IsNullOrWhiteSpace(payload.TenantAccessToken))
                throw new InvalidOperationException(payload.Msg ?? $"Feishu tenant token error {payload.Code}.");

            _token = payload.TenantAccessToken;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(MinimumTokenLifetimeSeconds, payload.Expire - TokenRefreshBufferSeconds));
            LastRefreshError = null;
            return _token;
        }
        catch (Exception ex)
        {
            LastRefreshError = ex;
            logger.LogError(ex, "Failed to refresh Feishu tenant access token.");
            throw;
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}

internal readonly record struct FeishuPreparedMessage(string MsgType, string Content);

public sealed class FeishuTenantAccessTokenRequest
{
    [JsonPropertyName("app_id")]
    public required string AppId { get; set; }

    [JsonPropertyName("app_secret")]
    public required string AppSecret { get; set; }
}

public sealed class FeishuTenantAccessTokenResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("tenant_access_token")]
    public string? TenantAccessToken { get; set; }

    [JsonPropertyName("expire")]
    public int Expire { get; set; } = 7200;
}

public sealed class FeishuSendMessageRequest
{
    [JsonPropertyName("receive_id")]
    public required string ReceiveId { get; set; }

    [JsonPropertyName("msg_type")]
    public required string MsgType { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public sealed class FeishuReplyMessageRequest
{
    [JsonPropertyName("msg_type")]
    public required string MsgType { get; set; }

    [JsonPropertyName("content")]
    public required string Content { get; set; }
}

public sealed class FeishuSendMessageResponse
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public FeishuSendMessageResponseData? Data { get; set; }
}

public sealed class FeishuSendMessageResponseData
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }
}

public sealed class FeishuWebhookEnvelope
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }

    [JsonPropertyName("encrypt")]
    public string? Encrypt { get; set; }

    [JsonPropertyName("header")]
    public FeishuWebhookHeader? Header { get; set; }

    [JsonPropertyName("event")]
    public FeishuMessageEvent? Event { get; set; }
}

public sealed class FeishuWebhookHeader
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

public sealed class FeishuMessageEvent
{
    [JsonPropertyName("sender")]
    public FeishuEventSender? Sender { get; set; }

    [JsonPropertyName("message")]
    public FeishuEventMessage? Message { get; set; }
}

public sealed class FeishuEventSender
{
    [JsonPropertyName("sender_id")]
    public FeishuSenderIdentity? SenderId { get; set; }

    [JsonPropertyName("sender_type")]
    public string? SenderType { get; set; }
}

public sealed class FeishuSenderIdentity
{
    [JsonPropertyName("open_id")]
    public string? OpenId { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("union_id")]
    public string? UnionId { get; set; }
}

public sealed class FeishuEventMessage
{
    [JsonPropertyName("message_id")]
    public string? MessageId { get; set; }

    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    [JsonPropertyName("root_id")]
    public string? RootId { get; set; }

    [JsonPropertyName("chat_id")]
    public string? ChatId { get; set; }

    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("mentions")]
    public FeishuMessageMention[]? Mentions { get; set; }
}

public sealed class FeishuMessageMention
{
    [JsonPropertyName("id")]
    public FeishuSenderIdentity? Id { get; set; }

    [JsonPropertyName("key")]
    public string? Key { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class FeishuTextContent
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public sealed class FeishuImageContent
{
    [JsonPropertyName("image_key")]
    public string? ImageKey { get; set; }
}

public sealed class FeishuFileContent
{
    [JsonPropertyName("file_key")]
    public string? FileKey { get; set; }

    [JsonPropertyName("file_name")]
    public string? FileName { get; set; }
}

public sealed class FeishuUrlVerificationResponse
{
    [JsonPropertyName("challenge")]
    public string? Challenge { get; set; }
}

public sealed class FeishuInteractiveCard
{
    [JsonPropertyName("config")]
    public FeishuInteractiveCardConfig? Config { get; set; }

    [JsonPropertyName("elements")]
    public FeishuInteractiveCardElement[] Elements { get; set; } = [];
}

public sealed class FeishuInteractiveCardConfig
{
    [JsonPropertyName("wide_screen_mode")]
    public bool WideScreenMode { get; set; }
}

public sealed class FeishuInteractiveCardElement
{
    [JsonPropertyName("tag")]
    public required string Tag { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

[JsonSerializable(typeof(FeishuTenantAccessTokenRequest))]
[JsonSerializable(typeof(FeishuTenantAccessTokenResponse))]
[JsonSerializable(typeof(FeishuSendMessageRequest))]
[JsonSerializable(typeof(FeishuReplyMessageRequest))]
[JsonSerializable(typeof(FeishuSendMessageResponse))]
[JsonSerializable(typeof(FeishuSendMessageResponseData))]
[JsonSerializable(typeof(FeishuWebhookEnvelope))]
[JsonSerializable(typeof(FeishuWebhookHeader))]
[JsonSerializable(typeof(FeishuMessageEvent))]
[JsonSerializable(typeof(FeishuEventSender))]
[JsonSerializable(typeof(FeishuSenderIdentity))]
[JsonSerializable(typeof(FeishuEventMessage))]
[JsonSerializable(typeof(FeishuMessageMention))]
[JsonSerializable(typeof(FeishuTextContent))]
[JsonSerializable(typeof(FeishuImageContent))]
[JsonSerializable(typeof(FeishuFileContent))]
[JsonSerializable(typeof(FeishuUrlVerificationResponse))]
[JsonSerializable(typeof(FeishuInteractiveCard))]
[JsonSerializable(typeof(FeishuInteractiveCardConfig))]
[JsonSerializable(typeof(FeishuInteractiveCardElement))]
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
public partial class FeishuJsonContext : JsonSerializerContext;
