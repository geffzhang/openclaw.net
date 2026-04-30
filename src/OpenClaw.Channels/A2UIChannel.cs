using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenClaw.Core.A2UI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Channels;

/// <summary>
/// WebSocket channel for A2UI JSON Lines instructions and client interaction events.
/// </summary>
public sealed class A2UIChannel : IChannelAdapter
{
    private readonly A2UIChannelConfig _config;
    private readonly ConcurrentDictionary<string, ConnectionState> _connections = new();
    private readonly ConcurrentDictionary<string, int> _connectionsPerIp = new();
    private readonly ComponentTypePolicy _componentPolicy;
    private int _connectionCount;

    private sealed class ConnectionState
    {
        public required WebSocket Socket { get; init; }
        public string IpKey { get; init; } = "unknown";
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public RateWindow Rate { get; }

        public ConnectionState(int messagesPerMinute)
        {
            Rate = new RateWindow(messagesPerMinute);
        }
    }

    private sealed class RateWindow
    {
        private readonly int _limit;
        private long _windowMinute;
        private int _count;
        private readonly object _gate = new();

        public RateWindow(int limit) => _limit = Math.Max(1, limit);

        public bool TryConsume()
        {
            lock (_gate)
            {
                var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                if (minute != _windowMinute)
                {
                    _windowMinute = minute;
                    _count = 0;
                }

                _count++;
                return _count <= _limit;
            }
        }
    }

    public A2UIChannel(A2UIChannelConfig config)
    {
        _config = config;
        _componentPolicy = ComponentTypePolicy.FromConfig(config.AllowedComponentTypes, config.AllowAnyComponentType);
    }

    public string ChannelId => "a2ui";

    public event Func<InboundMessage, CancellationToken, ValueTask>? OnMessageReceived;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task HandleConnectionAsync(WebSocket ws, string clientId, IPAddress? remoteIp, CancellationToken ct)
    {
        if (!TryAddConnection(clientId, ws, remoteIp))
        {
            await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "connection limit exceeded", ct);
            return;
        }

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReceiveFullTextMessageAsync(ws, ct);
                if (text is null)
                    break;

                if (!_connections.TryGetValue(clientId, out var state))
                    break;

                if (!state.Rate.TryConsume())
                {
                    await SendErrorAsync(clientId, "Rate limit exceeded", ct);
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "rate limit exceeded", ct);
                    break;
                }

                var parsed = TryParseClientEvent(text);
                if (parsed is null)
                {
                    await SendErrorAsync(clientId, "Invalid A2UI client event", ct);
                    continue;
                }

                if (OnMessageReceived is not null)
                    await OnMessageReceived(ToInboundMessage(parsed, clientId, ct), ct);
            }
        }
        finally
        {
            RemoveConnection(clientId);
        }
    }

    public async ValueTask SendAsync(OutboundMessage message, CancellationToken ct)
    {
        if (TryParseInstruction(message.Text, out var instruction, out _))
        {
            await SendInstructionAsync(message.RecipientId, instruction, ct);
            return;
        }

        await SendInstructionAsync(
            message.RecipientId,
            new A2UIInstruction
            {
                Type = "updateDataModel",
                SurfaceId = message.SessionId,
                Path = "/assistant/latest",
                Value = CloneStringElement(message.Text),
                MessageId = message.ReplyToMessageId
            },
            ct);
    }

    public async ValueTask SendInstructionAsync(string recipientId, A2UIInstruction instruction, CancellationToken ct)
    {
        if (!_connections.TryGetValue(recipientId, out var state))
            return;

        var validationError = ValidateInstruction(instruction);
        if (validationError is not null)
            throw new InvalidOperationException(validationError);

        var payload = JsonSerializer.SerializeToUtf8Bytes(instruction, CoreJsonContext.Default.A2UIInstruction);
        if (payload.Length > _config.MaxInstructionBytes)
            throw new InvalidOperationException("A2UI instruction exceeds MaxInstructionBytes.");

        var linePayload = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, linePayload, 0, payload.Length);
        linePayload[^1] = (byte)'\n';

        await SendPayloadAsync(recipientId, state, linePayload, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var clientId in _connections.Keys.ToArray())
            RemoveConnection(clientId);

        await ValueTask.CompletedTask;
    }

    internal bool TryAddConnectionForTest(string clientId, WebSocket ws, IPAddress? remoteIp)
        => TryAddConnection(clientId, ws, remoteIp);

    private bool TryAddConnection(string clientId, WebSocket ws, IPAddress? remoteIp)
    {
        var newCount = Interlocked.Increment(ref _connectionCount);
        if (newCount > _config.MaxConnections)
        {
            Interlocked.Decrement(ref _connectionCount);
            return false;
        }

        var ipKey = remoteIp?.ToString() ?? "unknown";
        var state = new ConnectionState(_config.MessagesPerMinutePerConnection)
        {
            Socket = ws,
            IpKey = ipKey
        };

        var perIp = _connectionsPerIp.AddOrUpdate(ipKey, 1, (_, c) => c + 1);
        if (perIp > _config.MaxConnectionsPerIp)
        {
            _connectionsPerIp.AddOrUpdate(ipKey, 0, (_, c) => Math.Max(0, c - 1));
            Interlocked.Decrement(ref _connectionCount);
            state.SendLock.Dispose();
            return false;
        }

        if (!_connections.TryAdd(clientId, state))
        {
            _connectionsPerIp.AddOrUpdate(ipKey, 0, (_, c) => Math.Max(0, c - 1));
            Interlocked.Decrement(ref _connectionCount);
            state.SendLock.Dispose();
            return false;
        }

        return true;
    }

    private void RemoveConnection(string clientId)
    {
        if (!_connections.TryRemove(clientId, out var state))
            return;

        Interlocked.Decrement(ref _connectionCount);
        _connectionsPerIp.AddOrUpdate(state.IpKey, 0, (_, c) => Math.Max(0, c - 1));
        try { state.Socket.Dispose(); } catch { }
        try { state.SendLock.Dispose(); } catch { }
    }

    private async ValueTask SendPayloadAsync(string recipientId, ConnectionState state, byte[] payload, CancellationToken ct)
    {
        try
        {
            await state.SendLock.WaitAsync(ct);
            if (!_connections.TryGetValue(recipientId, out var current) || !ReferenceEquals(current, state))
                return;

            if (state.Socket.State != WebSocketState.Open)
                return;

            await state.Socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (WebSocketException)
        {
        }
        finally
        {
            try { state.SendLock.Release(); } catch { }
        }
    }

    private async ValueTask SendErrorAsync(string recipientId, string message, CancellationToken ct)
    {
        await SendInstructionAsync(
            recipientId,
            new A2UIInstruction
            {
                Type = "updateDataModel",
                Path = "/errors/latest",
                Value = CloneStringElement(message)
            },
            ct);
    }

    private static JsonElement CloneStringElement(string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            writer.WriteStringValue(value);

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private async Task<string?> ReceiveFullTextMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        var total = 0;
        WebSocketMessageType? messageType = null;

        try
        {
            while (true)
            {
                if (total >= buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(Math.Min(_config.MaxMessageBytes, buffer.Length * 2));
                    Buffer.BlockCopy(buffer, 0, grown, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                ValueWebSocketReceiveResult result;
                using var timeoutCts = _config.ReceiveTimeoutSeconds > 0
                    ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                    : null;

                if (timeoutCts is not null)
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(_config.ReceiveTimeoutSeconds));

                try
                {
                    result = await ws.ReceiveAsync(buffer.AsMemory(total, buffer.Length - total), timeoutCts?.Token ?? ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return null;
                }
                catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
                {
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.PolicyViolation, "receive timeout", CancellationToken.None);
                    return null;
                }
                catch (ObjectDisposedException)
                {
                    return null;
                }
                catch (WebSocketException)
                {
                    return null;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    return null;

                messageType ??= result.MessageType;
                total += result.Count;

                if (total > _config.MaxMessageBytes)
                {
                    await CloseIfOpenAsync(ws, WebSocketCloseStatus.MessageTooBig, "message too big", ct);
                    return null;
                }

                if (result.EndOfMessage)
                    break;
            }

            return messageType == WebSocketMessageType.Text
                ? Encoding.UTF8.GetString(buffer, 0, total)
                : null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private A2UIClientEvent? TryParseClientEvent(string payload)
    {
        try
        {
            var clientEvent = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.A2UIClientEvent);
            if (clientEvent is null || string.IsNullOrWhiteSpace(clientEvent.Type))
                return null;

            return clientEvent;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InboundMessage ToInboundMessage(A2UIClientEvent clientEvent, string clientId, CancellationToken ct)
    {
        var text = clientEvent.Type;
        if (!string.IsNullOrWhiteSpace(clientEvent.ActionId))
            text += $": {clientEvent.ActionId}";

        if (clientEvent.Data is { } data && data.ValueKind != JsonValueKind.Undefined)
            text += "\n" + data.GetRawText();

        return new InboundMessage
        {
            ChannelId = "a2ui",
            SenderId = clientId,
            SessionId = clientEvent.SessionId,
            Type = clientEvent.Type,
            Text = text,
            MessageId = clientEvent.MessageId,
            RequestCancellation = ct
        };
    }

    private bool TryParseInstruction(string payload, out A2UIInstruction instruction, out string? error)
    {
        instruction = null!;
        error = null;

        try
        {
            var parsed = JsonSerializer.Deserialize(payload, CoreJsonContext.Default.A2UIInstruction);
            if (parsed is null)
            {
                error = "Instruction payload is empty.";
                return false;
            }

            var validationError = ValidateInstruction(parsed);
            if (validationError is not null)
            {
                error = validationError;
                return false;
            }

            instruction = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private string? ValidateInstruction(A2UIInstruction instruction)
    {
        if (!A2UIProtocol.IsSupportedInstructionType(instruction.Type))
            return $"Unsupported A2UI instruction type '{instruction.Type}'.";

        if (string.Equals(instruction.Type, "updateDataModel", StringComparison.Ordinal) &&
            !A2UIProtocol.IsSafeJsonPointer(instruction.Path))
        {
            return "A2UI data model update requires a safe RFC 6901 JSON Pointer path.";
        }

        if (string.Equals(instruction.Type, "updateComponents", StringComparison.Ordinal) &&
            instruction.Components is { } components &&
            !A2UIProtocol.ContainsOnlyAllowedComponents(components, _componentPolicy))
        {
            return "A2UI component payload contains a component type that is not allowed.";
        }

        return null;
    }

    private static ValueTask CloseIfOpenAsync(WebSocket ws, WebSocketCloseStatus status, string description, CancellationToken ct)
    {
        if (ws.State is not WebSocketState.Open and not WebSocketState.CloseReceived)
            return ValueTask.CompletedTask;

        return new ValueTask(ws.CloseAsync(status, description, ct));
    }
}
