using System.Text;
using System.Text.Json;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Tools;

internal sealed class TodoTool : IToolWithContext
{
    private readonly SessionMetadataStore _metadataStore;

    public TodoTool(SessionMetadataStore metadataStore)
    {
        _metadataStore = metadataStore;
    }

    public string Name => "todo";
    public string Description => "Manage a session-scoped todo list. Supports list/read, add, update, start, complete, remove, clear, and write.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "action":{"type":"string","enum":["list","read","add","update","start","complete","remove","clear","write"],"default":"list"},
        "id":{"type":"string"},
        "text":{"type":"string"},
        "content":{"type":"string"},
        "status":{"type":"string","enum":["pending","in_progress","completed"]},
        "priority":{"type":"string","enum":["high","medium","low"]},
        "notes":{"type":"string"},
        "todos":{
          "type":"array",
          "items":{
            "type":"object",
            "properties":{
              "id":{"type":"string"},
              "text":{"type":"string"},
              "content":{"type":"string"},
              "status":{"type":"string","enum":["pending","in_progress","completed"]},
              "priority":{"type":"string","enum":["high","medium","low"]},
              "notes":{"type":"string"}
            }
          }
        }
      },
      "required":["action"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: todo requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var action = TodoToolSupport.GetString(root, "action") ?? "list";

        var metadata = _metadataStore.Get(context.Session.Id);
        var todos = metadata.TodoItems.ToList();

        switch (action)
        {
            case "list":
            case "read":
                return ValueTask.FromResult(TodoToolSupport.Render(todos));
            case "add":
            {
                var text = TodoToolSupport.GetTodoText(root);
                if (string.IsNullOrWhiteSpace(text))
                    return ValueTask.FromResult("Error: text is required.");

                var status = TodoToolSupport.GetStatus(root, SessionTodoStatus.Pending, out var statusError);
                if (statusError is not null)
                    return ValueTask.FromResult(statusError);

                var priority = TodoToolSupport.GetPriority(root, SessionTodoPriority.Medium, out var priorityError);
                if (priorityError is not null)
                    return ValueTask.FromResult(priorityError);

                var now = DateTimeOffset.UtcNow;
                todos.Add(new SessionTodoItem
                {
                    Id = TodoToolSupport.NewTodoId(),
                    Text = text.Trim(),
                    Status = status,
                    Priority = priority,
                    Completed = status == SessionTodoStatus.Completed,
                    Notes = TodoToolSupport.GetString(root, "notes"),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
                break;
            }
            case "update":
            case "start":
            case "complete":
            case "remove":
            {
                var id = TodoToolSupport.GetString(root, "id");
                if (string.IsNullOrWhiteSpace(id))
                    return ValueTask.FromResult("Error: id is required.");

                var index = todos.FindIndex(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (index < 0)
                    return ValueTask.FromResult($"Error: todo '{id}' was not found.");

                if (action == "remove")
                {
                    todos.RemoveAt(index);
                    break;
                }

                var existing = todos[index];
                var status = action switch
                {
                    "start" => SessionTodoStatus.InProgress,
                    "complete" => SessionTodoStatus.Completed,
                    _ => TodoToolSupport.GetStatus(root, existing.Status, out var error) is var parsedStatus && error is null
                        ? parsedStatus
                        : null
                };
                if (status is null)
                    return ValueTask.FromResult(TodoToolSupport.GetInvalidStatusError(root));

                var priority = TodoToolSupport.GetPriority(root, existing.Priority, out var priorityError);
                if (priorityError is not null)
                    return ValueTask.FromResult(priorityError);

                todos[index] = new SessionTodoItem
                {
                    Id = existing.Id,
                    Text = TodoToolSupport.GetTodoText(root) ?? existing.Text,
                    Status = status,
                    Priority = priority,
                    Notes = TodoToolSupport.GetString(root, "notes") ?? existing.Notes,
                    Completed = status == SessionTodoStatus.Completed,
                    CreatedAtUtc = existing.CreatedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                break;
            }
            case "write":
            {
                var parsed = TodoToolSupport.ParseTodoList(root, todos, out var error);
                if (error is not null)
                    return ValueTask.FromResult(error);

                todos = parsed;
                break;
            }
            case "clear":
                todos.Clear();
                break;
            default:
                return ValueTask.FromResult("Error: Unknown action. Valid actions are list, read, add, update, start, complete, remove, clear, and write.");
        }

        var inProgressCount = todos.Count(static item => item.Status == SessionTodoStatus.InProgress);
        if (inProgressCount > 1)
            return ValueTask.FromResult("Error: only one todo can be in_progress.");

        _metadataStore.Set(context.Session.Id, new SessionMetadataUpdateRequest
        {
            ActivePresetId = metadata.ActivePresetId,
            Starred = metadata.Starred,
            Tags = metadata.Tags,
            TodoItems = todos
        });

        return ValueTask.FromResult(TodoToolSupport.Render(todos));
    }
}

internal sealed class TodoReadTool : IToolWithContext
{
    private readonly SessionMetadataStore _metadataStore;

    public TodoReadTool(SessionMetadataStore metadataStore)
    {
        _metadataStore = metadataStore;
    }

    public string Name => "todo_read";
    public string Description => "Read the current session todo list for agent planning and progress tracking.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{}
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: todo_read requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
        => ValueTask.FromResult(TodoToolSupport.Render(_metadataStore.Get(context.Session.Id).TodoItems));
}

internal sealed class TodoWriteTool : IToolWithContext
{
    private readonly SessionMetadataStore _metadataStore;

    public TodoWriteTool(SessionMetadataStore metadataStore)
    {
        _metadataStore = metadataStore;
    }

    public string Name => "todo_write";
    public string Description => "Replace the session todo list with Claude-style items. Use pending, in_progress, or completed status and high, medium, or low priority.";
    public string ParameterSchema => """
    {
      "type":"object",
      "properties":{
        "todos":{
          "type":"array",
          "items":{
            "type":"object",
            "properties":{
              "id":{"type":"string"},
              "content":{"type":"string"},
              "text":{"type":"string"},
              "status":{"type":"string","enum":["pending","in_progress","completed"]},
              "priority":{"type":"string","enum":["high","medium","low"]},
              "notes":{"type":"string"}
            },
            "required":["content","status","priority"]
          }
        }
      },
      "required":["todos"]
    }
    """;

    public ValueTask<string> ExecuteAsync(string argumentsJson, CancellationToken ct)
        => ValueTask.FromResult("Error: todo_write requires execution context.");

    public ValueTask<string> ExecuteAsync(string argumentsJson, ToolExecutionContext context, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var root = document.RootElement;
        var metadata = _metadataStore.Get(context.Session.Id);
        var todos = TodoToolSupport.ParseTodoList(root, metadata.TodoItems, out var error);
        if (error is not null)
            return ValueTask.FromResult(error);

        _metadataStore.Set(context.Session.Id, new SessionMetadataUpdateRequest
        {
            ActivePresetId = metadata.ActivePresetId,
            Starred = metadata.Starred,
            Tags = metadata.Tags,
            TodoItems = todos
        });

        return ValueTask.FromResult(TodoToolSupport.Render(todos));
    }
}

internal static class TodoToolSupport
{
    public static string NewTodoId() => $"todo_{Guid.NewGuid():N}"[..17];

    public static string Render(IReadOnlyList<SessionTodoItem> todos)
    {
        if (todos.Count == 0)
            return "No todo items.";

        var sb = new StringBuilder();
        foreach (var todo in todos)
            sb.AppendLine($"{todo.Id} [{NormalizeStatus(todo)}:{NormalizePriority(todo.Priority)}] {todo.Text}");
        return sb.ToString().TrimEnd();
    }

    public static List<SessionTodoItem> ParseTodoList(JsonElement root, IReadOnlyList<SessionTodoItem> existingTodos, out string? error)
    {
        error = null;
        if (!root.TryGetProperty("todos", out var todosElement) || todosElement.ValueKind != JsonValueKind.Array)
        {
            error = "Error: todos array is required.";
            return [];
        }

        var existingById = existingTodos.ToDictionary(static item => item.Id, StringComparer.Ordinal);
        var parsed = new List<SessionTodoItem>();
        foreach (var item in todosElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "Error: each todo must be an object.";
                return [];
            }

            var text = GetTodoText(item);
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "Error: each todo requires content.";
                return [];
            }

            var status = GetStatus(item, SessionTodoStatus.Pending, out error);
            if (error is not null)
                return [];

            var priority = GetPriority(item, SessionTodoPriority.Medium, out error);
            if (error is not null)
                return [];

            var id = GetString(item, "id");
            var now = DateTimeOffset.UtcNow;
            var existing = id is not null && existingById.TryGetValue(id, out var match) ? match : null;
            parsed.Add(new SessionTodoItem
            {
                Id = string.IsNullOrWhiteSpace(id) ? NewTodoId() : id.Trim(),
                Text = text.Trim(),
                Status = status,
                Priority = priority,
                Completed = status == SessionTodoStatus.Completed,
                Notes = GetString(item, "notes"),
                CreatedAtUtc = existing?.CreatedAtUtc ?? now,
                UpdatedAtUtc = now
            });
        }

        if (parsed.Count(static item => item.Status == SessionTodoStatus.InProgress) > 1)
        {
            error = "Error: only one todo can be in_progress.";
            return [];
        }

        return parsed;
    }

    public static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    public static string? GetTodoText(JsonElement root)
        => GetString(root, "content") ?? GetString(root, "text");

    public static string GetStatus(JsonElement root, string defaultStatus, out string? error)
    {
        error = null;
        var status = GetString(root, "status");
        if (string.IsNullOrWhiteSpace(status))
            return defaultStatus;

        return status.Trim() switch
        {
            SessionTodoStatus.Pending => SessionTodoStatus.Pending,
            SessionTodoStatus.InProgress => SessionTodoStatus.InProgress,
            SessionTodoStatus.Completed => SessionTodoStatus.Completed,
            _ => InvalidStatus(out error)
        };
    }

    public static string GetPriority(JsonElement root, string defaultPriority, out string? error)
    {
        error = null;
        var priority = GetString(root, "priority");
        if (string.IsNullOrWhiteSpace(priority))
            return defaultPriority;

        return priority.Trim() switch
        {
            SessionTodoPriority.High => SessionTodoPriority.High,
            SessionTodoPriority.Medium => SessionTodoPriority.Medium,
            SessionTodoPriority.Low => SessionTodoPriority.Low,
            _ => InvalidPriority(out error)
        };
    }

    public static string GetInvalidStatusError(JsonElement root)
    {
        _ = GetStatus(root, SessionTodoStatus.Pending, out var error);
        return error ?? "Error: invalid status.";
    }

    private static string NormalizeStatus(SessionTodoItem item)
        => string.IsNullOrWhiteSpace(item.Status)
            ? item.Completed ? SessionTodoStatus.Completed : SessionTodoStatus.Pending
            : item.Status;

    private static string NormalizePriority(string? priority)
        => string.IsNullOrWhiteSpace(priority) ? SessionTodoPriority.Medium : priority;

    private static string InvalidStatus(out string? error)
    {
        error = "Error: status must be pending, in_progress, or completed.";
        return "";
    }

    private static string InvalidPriority(out string? error)
    {
        error = "Error: priority must be high, medium, or low.";
        return "";
    }
}
