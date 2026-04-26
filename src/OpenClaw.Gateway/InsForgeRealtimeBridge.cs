using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Channels;
using OpenClaw.Core.Models;
using OpenClaw.Core.Security;

namespace OpenClaw.Gateway;

internal sealed class InsForgeRealtimeBridge
{
    private readonly InsForgeConfig _config;
    private readonly A2UIChannel _a2uiChannel;
    private readonly ILogger _logger;

    public InsForgeRealtimeBridge(InsForgeConfig config, A2UIChannel a2uiChannel, ILogger logger)
    {
        _config = config;
        _a2uiChannel = a2uiChannel;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_config.Enabled || string.IsNullOrWhiteSpace(_config.RealtimeUrl))
            return;

        if (!Uri.TryCreate(_config.RealtimeUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss"))
        {
            _logger.LogWarning("InsForge Realtime bridge disabled because RealtimeUrl is not a ws(s) URL.");
            return;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                var apiKey = SecretResolver.Resolve(_config.ApiKeyRef);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    ws.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);

                await ws.ConnectAsync(uri, ct);

                if (!string.IsNullOrWhiteSpace(_config.RealtimeSubscribePayload))
                {
                    var subscribePayload = Encoding.UTF8.GetBytes(_config.RealtimeSubscribePayload);
                    await ws.SendAsync(subscribePayload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
                }

                await ReceiveLoopAsync(ws, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "InsForge Realtime bridge disconnected; retrying.");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return;

            if (!result.EndOfMessage)
            {
                _logger.LogWarning("Skipping oversized fragmented InsForge Realtime message.");
                return;
            }

            var payload = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (!TryBuildUpdate(payload, _config, out var recipientId, out var instruction, out var error))
            {
                _logger.LogDebug("Skipping InsForge Realtime message: {Reason}", error);
                continue;
            }

            await _a2uiChannel.SendInstructionAsync(recipientId, instruction, ct);
        }
    }

    internal static bool TryBuildUpdate(
        string payload,
        InsForgeConfig config,
        out string recipientId,
        out A2UIInstruction instruction,
        out string error)
    {
        recipientId = "";
        instruction = null!;
        error = "";

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var record = root.TryGetProperty("record", out var recordElement) &&
                         recordElement.ValueKind == JsonValueKind.Object
                ? recordElement
                : root;

            recipientId = GetString(record, config.RealtimeRecipientIdProperty)
                          ?? GetString(record, config.RealtimeSessionIdProperty)
                          ?? "";
            if (string.IsNullOrWhiteSpace(recipientId))
            {
                error = "missing recipient/session id";
                return false;
            }

            var path = GetString(record, config.RealtimePathProperty);
            if (string.IsNullOrWhiteSpace(path))
            {
                var sessionId = GetString(record, config.RealtimeSessionIdProperty);
                var id = GetString(record, "id") ?? "latest";
                path = BuildPointer(config.RealtimeJsonPointerPrefix, sessionId, id);
            }

            if (!A2UIProtocol.IsSafeJsonPointer(path))
            {
                error = "unsafe JSON Pointer";
                return false;
            }

            var value = record.TryGetProperty(config.RealtimeValueProperty, out var valueElement)
                ? valueElement.Clone()
                : record.Clone();

            instruction = new A2UIInstruction
            {
                Type = "updateDataModel",
                Path = path,
                Value = value
            };
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string BuildPointer(string prefix, string? sessionId, string id)
    {
        var safePrefix = A2UIProtocol.IsSafeJsonPointer(prefix) ? prefix.TrimEnd('/') : "/insforge";
        var escapedSessionId = EscapePointerSegment(string.IsNullOrWhiteSpace(sessionId) ? "session" : sessionId);
        var escapedId = EscapePointerSegment(id);
        return $"{safePrefix}/{escapedSessionId}/{escapedId}";
    }

    private static string EscapePointerSegment(string value)
        => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
