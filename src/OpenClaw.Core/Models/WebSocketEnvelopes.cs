namespace OpenClaw.Core.Models;

/// <summary>
/// Optional JSON envelope used by WebSocket clients.
/// Raw-text clients may continue sending plain text.
/// </summary>
public sealed record WsClientEnvelope
{
    public required string Type { get; init; }
    public string? RequestId { get; init; }
    public string? Text { get; init; }
    public string? Content { get; init; }
    public string? SessionId { get; init; }
    public string? MessageId { get; init; }
    public string? ReplyToMessageId { get; init; }
    public string? SurfaceId { get; init; }
    public string? ContentType { get; init; }
    public string? Frames { get; init; }
    public string? Html { get; init; }
    public string? Url { get; init; }
    public string? Script { get; init; }
    public string? SnapshotMode { get; init; }
    public string? SnapshotJson { get; init; }
    public string? ComponentId { get; init; }
    public string? Event { get; init; }
    public string? ValueJson { get; init; }
    public long? Sequence { get; init; }
    public string[]? Capabilities { get; init; }
    public string? Error { get; init; }
    public bool? Success { get; init; }

    // Tool approval decision (client -> server)
    public string? ApprovalId { get; init; }
    public bool? Approved { get; init; }
}

/// <summary>
/// JSON envelope sent by the gateway when a client opts into envelopes.
/// </summary>
public sealed record WsServerEnvelope
{
    public required string Type { get; init; }
    public string? RequestId { get; init; }
    public string? Text { get; init; }
    public string? InReplyToMessageId { get; init; }
    public string? SessionId { get; init; }
    public string? SurfaceId { get; init; }
    public string? ContentType { get; init; }
    public string? Frames { get; init; }
    public string? Html { get; init; }
    public string? Url { get; init; }
    public string? Script { get; init; }
    public string? SnapshotMode { get; init; }
    public string? SnapshotJson { get; init; }
    public string? ComponentId { get; init; }
    public string? Event { get; init; }
    public string? ValueJson { get; init; }
    public long? Sequence { get; init; }
    public string[]? Capabilities { get; init; }
    public string? Error { get; init; }
    public bool? Success { get; init; }

    // A2UI v0.9 instruction fields (carried via type="a2ui_instruction" Canvas envelope).
    // These are nullable additions that v0.8 clients safely ignore.
    public string? Path { get; init; }
    public System.Text.Json.JsonElement? Components { get; init; }
    public System.Text.Json.JsonElement? Value { get; init; }
    public string? InstructionType { get; init; }

    // Tool approval request/status (server -> client)
    public string? ApprovalId { get; init; }
    public string? ToolName { get; init; }
    public string? ArgumentsPreview { get; init; }
    public bool? Approved { get; init; }
    public string? ResultStatus { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }
    public string? NextStep { get; init; }
}
