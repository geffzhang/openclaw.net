using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Core.Observability;
using OpenClaw.Gateway;
using OpenClaw.Gateway.Tools;
using Xunit;

namespace OpenClaw.Tests;

public sealed class TodoToolTests
{
    [Fact]
    public async Task ExecuteAsync_AddStartAndComplete_PersistsClaudeStyleTodoState()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var tool = new TodoTool(metadataStore);
        var context = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_todo",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_todo",
                ChannelId = "websocket"
            }
        };

        var addResult = await tool.ExecuteAsync("""{"action":"add","content":"Review deployment notes","priority":"high"}""", context, CancellationToken.None);
        var todoId = addResult.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.Contains("Review deployment notes", addResult, StringComparison.Ordinal);

        var startResult = await tool.ExecuteAsync($$"""{"action":"start","id":"{{todoId}}"}""", context, CancellationToken.None);
        Assert.Contains("[in_progress:high]", startResult, StringComparison.Ordinal);

        var completeResult = await tool.ExecuteAsync($$"""{"action":"complete","id":"{{todoId}}"}""", context, CancellationToken.None);
        Assert.Contains("[completed:high]", completeResult, StringComparison.Ordinal);

        var metadata = metadataStore.Get("sess_todo");
        var todo = Assert.Single(metadata.TodoItems);
        Assert.Equal(todoId, todo.Id);
        Assert.True(todo.Completed);
        Assert.Equal(SessionTodoStatus.Completed, todo.Status);
        Assert.Equal(SessionTodoPriority.High, todo.Priority);
    }

    [Fact]
    public async Task TodoReadAndWrite_ReplaceFullListAndRejectMultipleInProgress()
    {
        var storagePath = Path.Combine(Path.GetTempPath(), "openclaw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);
        var metadataStore = new SessionMetadataStore(storagePath, NullLogger<SessionMetadataStore>.Instance);
        var readTool = new TodoReadTool(metadataStore);
        var writeTool = new TodoWriteTool(metadataStore);
        var context = new ToolExecutionContext
        {
            Session = new Session
            {
                Id = "sess_todo_write",
                ChannelId = "websocket",
                SenderId = "user1"
            },
            TurnContext = new TurnContext
            {
                SessionId = "sess_todo_write",
                ChannelId = "websocket"
            }
        };

        var writeResult = await writeTool.ExecuteAsync("""
        {
          "todos":[
            {"content":"Plan work","status":"completed","priority":"high"},
            {"content":"Implement change","status":"in_progress","priority":"medium"},
            {"content":"Validate change","status":"pending","priority":"low"}
          ]
        }
        """, context, CancellationToken.None);

        Assert.Contains("[completed:high] Plan work", writeResult, StringComparison.Ordinal);
        Assert.Contains("[in_progress:medium] Implement change", writeResult, StringComparison.Ordinal);
        Assert.Contains("[pending:low] Validate change", writeResult, StringComparison.Ordinal);

        var readResult = await readTool.ExecuteAsync("{}", context, CancellationToken.None);
        Assert.Contains("Implement change", readResult, StringComparison.Ordinal);

        var metadata = metadataStore.Get("sess_todo_write");
        Assert.Contains(metadata.TodoItems, static item => item.Status == SessionTodoStatus.Completed);
        Assert.Contains(metadata.TodoItems, static item => item.Status == SessionTodoStatus.InProgress);
        Assert.Contains(metadata.TodoItems, static item => item.Status == SessionTodoStatus.Pending);

        var invalidResult = await writeTool.ExecuteAsync("""
        {
          "todos":[
            {"content":"First","status":"in_progress","priority":"medium"},
            {"content":"Second","status":"in_progress","priority":"medium"}
          ]
        }
        """, context, CancellationToken.None);

        Assert.Contains("only one todo can be in_progress", invalidResult, StringComparison.Ordinal);
    }
}
