using System.Collections.Concurrent;
using System.Text.Json;
using BotSharp.Abstraction.Agents.Enums;
using BotSharp.Abstraction.Conversations;
using BotSharp.Abstraction.Conversations.Models;
using BotSharp.Abstraction.Models;
using Microsoft.Extensions.Logging;
using OpenClaw.Agent;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Runtime;

public sealed class BotSharpRuntimeAdapter(
    IServiceScopeFactory scopeFactory,
    ILogger<BotSharpRuntimeAdapter> logger,
    string agentId) : IAgentRuntime
{
    private readonly ConcurrentDictionary<string, string> _conversationMap = new(StringComparer.Ordinal);

    public async Task<string> RunAsync(
        Session session,
        string userMessage,
        CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null,
        JsonElement? responseSchema = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        session.History.Add(new ChatTurn { Role = "user", Content = userMessage });
        var response = await SendAsync(session, userMessage, ct);
        session.History.Add(new ChatTurn { Role = "assistant", Content = response });
        return response;
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        Session session,
        string userMessage,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct,
        ToolApprovalCallback? approvalCallback = null)
    {
        var response = await RunAsync(session, userMessage, ct, approvalCallback);
        if (!string.IsNullOrEmpty(response))
            yield return AgentStreamEvent.TextDelta(response);
        yield return AgentStreamEvent.Complete();
    }

    private async Task<string> SendAsync(Session session, string userMessage, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var conversationService = scope.ServiceProvider.GetService<IConversationService>()
            ?? throw new InvalidOperationException("BotSharp IConversationService is not registered. Configure PluginLoader and BotSharp services.");

        await EnsureConversationAsync(conversationService, session, ct);

        var request = new RoleDialogModel
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Role = AgentRole.User,
            SenderId = session.SenderId,
            Content = userMessage,
            CreatedAt = DateTime.UtcNow
        };

        string? assistantResponse = null;
        var sent = await conversationService.SendMessage(
            agentId,
            request,
            null,
            message =>
            {
                if (message.Role == AgentRole.Assistant || message.Role == AgentRole.Model)
                    assistantResponse = message.Content;
                return Task.CompletedTask;
            });

        if (!sent)
            throw new InvalidOperationException("BotSharp conversation engine rejected the request.");

        return assistantResponse ?? string.Empty;
    }

    private async Task EnsureConversationAsync(IConversationService conversationService, Session session, CancellationToken ct)
    {
        if (_conversationMap.TryGetValue(session.Id, out var existingConversationId))
        {
            conversationService.SetConversationId(existingConversationId, new List<MessageState>(), false);
            return;
        }

        var conversation = await conversationService.NewConversation(new Conversation
        {
            AgentId = agentId,
            UserId = session.SenderId,
            ChannelId = session.ChannelId
        });

        if (string.IsNullOrWhiteSpace(conversation.Id))
        {
            logger.LogWarning("BotSharp returned an empty conversation id for session {SessionId}", session.Id);
            throw new InvalidOperationException($"BotSharp returned an empty conversation id for session {session.Id}.");
        }

        _conversationMap[session.Id] = conversation.Id;
    }
}
