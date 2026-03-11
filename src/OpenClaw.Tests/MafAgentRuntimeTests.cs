using Microsoft.Extensions.AI;
using NSubstitute;
using OpenClaw.Agent;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Skills;
using Xunit;

namespace OpenClaw.Tests;

public class MafAgentRuntimeTests
{
    private readonly IChatClient _chatClient;
    private readonly IMemoryStore _memory;
    private readonly List<ITool> _tools;
    private readonly LlmProviderConfig _config;

    public MafAgentRuntimeTests()
    {
        _chatClient = Substitute.For<IChatClient>();
        _memory = Substitute.For<IMemoryStore>();
        _tools = new List<ITool>();
        _config = new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" };

        // Mock default behavior for ChatClient (used by ChatClientAgent internally)
        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "Hello from MAF") })));
    }

    [Fact]
    public async Task RunAsync_SingleTurn_ReturnsResponse()
    {
        var agent = new MafAgentRuntime(_chatClient, _tools, _memory, _config, maxHistoryTurns: 5);
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };

        var result = await agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.Equal("Hello from MAF", result);
        Assert.Contains(session.History, t => t.Role == "user" && t.Content == "Hello");
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello from MAF");
    }

    [Fact]
    public async Task RunAsync_TrimsHistory()
    {
        var agent = new MafAgentRuntime(_chatClient, _tools, _memory, _config, maxHistoryTurns: 5);
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };

        for (int i = 0; i < 10; i++)
        {
            session.History.Add(new ChatTurn { Role = "user", Content = $"msg {i}" });
        }

        await agent.RunAsync(session, "New message", CancellationToken.None);

        // RunAsync adds user msg (11), trims to 5, then adds assistant (6)
        Assert.True(session.History.Count <= 6, $"Expected history <= 6 but was {session.History.Count}");
    }

    [Fact]
    public async Task RunStreamingAsync_SingleTurn_YieldsTextAndComplete()
    {
        // Set up streaming response
        var updates = new List<ChatResponseUpdate>
        {
            new(ChatRole.Assistant, "Hello "),
            new(ChatRole.Assistant, "from MAF")
        };

        _chatClient.GetStreamingResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(updates.ToAsyncEnumerable());

        var agent = new MafAgentRuntime(_chatClient, _tools, _memory, _config, maxHistoryTurns: 5);
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.RunStreamingAsync(session, "Hello", CancellationToken.None))
        {
            events.Add(evt);
        }

        // Expect text deltas and a complete event
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "Hello ");
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta && e.Content == "from MAF");
        Assert.Contains(events, e => e.Type == AgentStreamEventType.Done);

        // Verify session history is updated
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello from MAF");
    }

    [Fact]
    public void CircuitBreakerState_AlwaysClosed()
    {
        var agent = new MafAgentRuntime(_chatClient, _tools, _memory, _config, maxHistoryTurns: 5);
        Assert.Equal(CircuitState.Closed, agent.CircuitBreakerState);
    }

    [Fact]
    public async Task ReloadSkillsAsync_UpdatesLoadedSkillNames()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"openclaw-maf-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(workspaceDir, "skills", "reloadable");
        Directory.CreateDirectory(skillDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                """
                ---
                name: maf-skill
                description: MAF test skill
                ---
                Use this skill after reload.
                """);

            var agent = new MafAgentRuntime(
                _chatClient,
                _tools,
                _memory,
                _config,
                maxHistoryTurns: 5,
                skillsConfig: new SkillsConfig
                {
                    Load = new SkillLoadConfig
                    {
                        IncludeBundled = false,
                        IncludeManaged = false,
                        IncludeWorkspace = true
                    }
                },
                skillWorkspacePath: workspaceDir);

            Assert.Empty(agent.LoadedSkillNames);

            var loaded = await agent.ReloadSkillsAsync();

            Assert.Single(loaded);
            Assert.Contains("maf-skill", loaded);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }

    [Fact]
    public void UseMaf_ConfigOption_DefaultsToFalse()
    {
        var config = new LlmProviderConfig();
        Assert.False(config.UseMaf);
    }
}
