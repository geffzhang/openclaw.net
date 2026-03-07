using BotSharp.Abstraction.Agents.Enums;
using BotSharp.Abstraction.Conversations;
using BotSharp.Abstraction.Conversations.Models;
using BotSharp.Abstraction.MessageHub.Models;
using BotSharp.Abstraction.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenClaw.Gateway.Runtime;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public class BotSharpRuntimeAdapterTests
{
    [Fact]
    public async Task RunAsync_MapsSessionAndReusesConversation()
    {
        var conversation = Substitute.For<IConversationService>();
        conversation.NewConversation(Arg.Any<Conversation>())
            .Returns(Task.FromResult(new Conversation { Id = "conv-1" }));
        conversation.SendMessage(
                "router",
                Arg.Any<RoleDialogModel>(),
                Arg.Any<PostbackMessageModel?>(),
                Arg.Any<Func<RoleDialogModel, Task>>())
            .Returns(async call =>
            {
                var callback = call.ArgAt<Func<RoleDialogModel, Task>>(3);
                await callback(new RoleDialogModel(AgentRole.Assistant, "bot-reply"));
                return true;
            });

        var scopeFactory = BuildScopeFactory(conversation);
        var sut = new BotSharpRuntimeAdapter(scopeFactory, NullLogger<BotSharpRuntimeAdapter>.Instance, "router");
        var session = new Session { Id = "s-1", ChannelId = "websocket", SenderId = "user-1" };

        var first = await sut.RunAsync(session, "hello", CancellationToken.None);
        var second = await sut.RunAsync(session, "hello-again", CancellationToken.None);

        Assert.Equal("bot-reply", first);
        Assert.Equal("bot-reply", second);
        conversation.Received(1).SetConversationId("conv-1", Arg.Any<List<MessageState>>(), false);
    }

    [Fact]
    public async Task RunStreamingAsync_EmitsTextAndDone()
    {
        var conversation = Substitute.For<IConversationService>();
        conversation.NewConversation(Arg.Any<Conversation>())
            .Returns(Task.FromResult(new Conversation { Id = "conv-stream" }));
        conversation.SendMessage(
                "router",
                Arg.Any<RoleDialogModel>(),
                Arg.Any<PostbackMessageModel?>(),
                Arg.Any<Func<RoleDialogModel, Task>>())
            .Returns(async call =>
            {
                var callback = call.ArgAt<Func<RoleDialogModel, Task>>(3);
                await callback(new RoleDialogModel(AgentRole.Assistant, "stream-reply"));
                return true;
            });

        var scopeFactory = BuildScopeFactory(conversation);
        var sut = new BotSharpRuntimeAdapter(scopeFactory, NullLogger<BotSharpRuntimeAdapter>.Instance, "router");
        var session = new Session { Id = "s-2", ChannelId = "websocket", SenderId = "user-2" };

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in sut.RunStreamingAsync(session, "go", CancellationToken.None))
            events.Add(evt);

        Assert.Equal(2, events.Count);
        Assert.Equal(AgentStreamEventType.TextDelta, events[0].Type);
        Assert.Equal("stream-reply", events[0].Content);
        Assert.Equal(AgentStreamEventType.Done, events[1].Type);
    }

    private static IServiceScopeFactory BuildScopeFactory(IConversationService conversation)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => conversation);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
