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
    private readonly MafAgentRuntime _agent;
    private readonly LlmProviderConfig _config;

    public MafAgentRuntimeTests()
    {
        _chatClient = Substitute.For<IChatClient>();
        _memory = Substitute.For<IMemoryStore>();
        _tools = new List<ITool>();
        _config = new LlmProviderConfig { Provider = "openai", ApiKey = "test", Model = "gpt-4" };

        _chatClient.GetResponseAsync(
            Arg.Any<IList<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new[] { new ChatMessage(ChatRole.Assistant, "Hello from AI") })));

        _agent = MafTestRuntimeFactory.CreateRuntime(
            _chatClient,
            _memory,
            _tools,
            _config,
            maxHistoryTurns: 5);
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_SingleTurn_ReturnsResponse()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        var result = await _agent.RunAsync(session, "Hello", CancellationToken.None);

        Assert.Equal("Hello from AI", result);
        Assert.Contains(session.History, t => t.Role == "user" && t.Content == "Hello");
        Assert.Contains(session.History, t => t.Role == "assistant" && t.Content == "Hello from AI");
    }

    [Fact]
    public async Task MafAgentRuntime_RunAsync_TrimsHistory()
    {
        var session = new Session { Id = "sess1", SenderId = "user1", ChannelId = "test-channel" };
        for (int i = 0; i < 10; i++)
            session.History.Add(new ChatTurn { Role = "user", Content = $"msg {i}" });

        await _agent.RunAsync(session, "New message", CancellationToken.None);

        Assert.True(session.History.Count <= 6, $"Expected history <= 6 but was {session.History.Count}");
    }

    [Fact]
    public async Task MafAgentRuntime_ReloadSkillsAsync_UpdatesLoadedSkillNames()
    {
        var workspaceDir = Path.Combine(Path.GetTempPath(), $"openclaw-skills-{Guid.NewGuid():N}");
        var skillDir = Path.Combine(workspaceDir, "skills", "reloadable");
        Directory.CreateDirectory(skillDir);

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(skillDir, "SKILL.md"),
                """
                ---
                name: reloadable-skill
                description: Hot reloaded during tests
                ---
                Use this skill after reload.
                """);

            var agent = MafTestRuntimeFactory.CreateRuntime(
                _chatClient,
                _memory,
                _tools,
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
            Assert.Contains("reloadable-skill", loaded);
        }
        finally
        {
            Directory.Delete(workspaceDir, recursive: true);
        }
    }
}
