# Tool Declaration Reduction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a tool declaration reduction layer that lowers pre-LLM function/tool schema cost, is shared by native `AgentRuntime` and `MafAgentRuntime`, and includes both the AOT-safe rule reducer and a self-contained semantic reducer plugin.

**Architecture:** Keep tool permissions and presets as hard boundaries, then rank and cap only the preset-allowed declarations inside `OpenClawToolExecutor`. The first stage implements the core rule-based reducer. The second stage adds an OpenClaw-owned semantic reducer plugin that borrows the ElBruno MCPToolRouter design ideas (tool index, prompt intent distillation, hybrid search) without referencing or packaging ElBruno code or packages.

**Tech Stack:** .NET 10, C#, `Microsoft.Extensions.AI.Abstractions`, xUnit v3, NSubstitute, existing OpenClaw Core/Agent/Gateway composition, optional OpenClaw-owned semantic plugin project.

## Global Constraints

- Core runtime must remain lightweight and NativeAOT friendly.
- Optional integrations must remain optional.
- Public-bind hardening and governance take precedence over convenience.
- Tool execution paths remain behind the existing tool execution layer.
- Compatibility claims require tests across native and MAF paths.
- Default behavior must remain equivalent to current behavior because declaration reduction is disabled by default.
- Default reduction values are `Enabled=false`, `Mode="rule"`, `MaxTools=16`, `MinTools=4`, `HardMaxTools=24`, `MinScore=0.10`, `FallbackToPresetOnEmpty=true`, `FallbackToRuleWhenSemanticUnavailable=true`, and `EnablePromptDistillation=false`.
- Reducers must never add tools excluded by `RouteToolsDisabled`, `Session.RouteAllowedTools`, resolved presets, or governance policy.
- The core implementation must not reference embedding, local LLM, ONNX, ElBruno, or MCP router packages.
- The semantic plugin must be OpenClaw-owned code. It may borrow architectural ideas from `ElBruno.ModelContextProtocol.MCPToolRouter`, but it must not reference `ElBruno.*`, `ModelContextProtocol.MCPToolRouter`, or copy source files verbatim.
- Semantic and hybrid modes must fail open to rule mode or preset-allowed candidates when the semantic plugin is missing, disabled, or unhealthy.

---

## File Structure

- Modify `src/OpenClaw.Core/Models/GatewayConfig.cs`: add `ToolDeclarationReductionConfig` and attach it to `ToolingConfig`.
- Modify `src/OpenClaw.Core/Models/Session.cs`: add source-generation metadata for the new config model.
- Create `src/OpenClaw.Core/Abstractions/IToolDeclarationReducer.cs`: shared reducer interface and request/context/result/diagnostics contracts.
- Create `src/OpenClaw.Agent/ToolDeclarations/RuleBasedToolDeclarationReducer.cs`: deterministic AOT-safe reducer implementation.
- Create `src/OpenClaw.Agent/ToolDeclarations/ToolDeclarationText.cs`: small helper for extracting searchable text from `AITool` declarations.
- Modify `src/OpenClaw.Agent/OpenClawToolExecutor.cs`: add reducer dependency, reduction-aware overload, diagnostics logging, and fail-open behavior.
- Modify `src/OpenClaw.Agent/AgentRuntime.cs`: pass the current user message into tool declaration selection before model calls and turn-routing probes.
- Modify `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs`: pass `IToolDeclarationReducer` from DI into native runtime construction.
- Modify `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`: pass the current user message into declaration selection in `ApplyTurnRoutingAsync` and `CreateAgent`.
- Modify `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`: register `RuleBasedToolDeclarationReducer` as `IToolDeclarationReducer`.
- Modify `src/OpenClaw.PluginKit/INativeDynamicPlugin.cs`: add a reducer registration extension point if the semantic reducer is delivered through the native dynamic plugin path.
- Modify `src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs`: collect dynamic plugin declaration reducers.
- Modify `src/OpenClaw.Agent/IAgentRuntimeFactory.cs`: allow runtime factory context to carry the effective reducer explicitly when plugin composition selects one.
- Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/`: self-contained semantic declaration reducer plugin project.
- Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/SemanticToolDeclarationReducer.cs`: semantic/hybrid reducer implementation.
- Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/SemanticToolIndex.cs`: in-memory tool vector index.
- Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/PromptIntentDistiller.cs`: local prompt-to-action-phrase distiller.
- Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/TextEmbedding/`: OpenClaw-owned embedding/vectorization helpers.
- Create `src/OpenClaw.Tests/ToolDeclarationReductionConfigTests.cs`: config defaults and JSON/source-generation coverage.
- Create `src/OpenClaw.Tests/RuleBasedToolDeclarationReducerTests.cs`: rule scoring and boundary tests.
- Create `src/OpenClaw.Tests/SemanticToolDeclarationReducerTests.cs`: semantic index, hybrid ranking, fallback, and dependency-boundary tests.
- Create `src/OpenClaw.Tests/NativeDynamicToolDeclarationReducerPluginTests.cs`: plugin registration and selection tests if using native dynamic plugin loading.
- Modify `src/OpenClaw.Tests/OpenClawToolExecutorTests.cs`: executor integration, fail-open, preset boundary, and disabled-mode tests.
- Modify `src/OpenClaw.Tests/MafAdapterTests.cs`: MAF parity test for reduced declarations.
- Create `docs/tool-declaration-reduction.md`: operator-facing documentation.
- Modify `docs/README.md`: add the new documentation entry.

---

### Task 1: Configuration Model and Serialization

**Files:**
- Modify: `src/OpenClaw.Core/Models/GatewayConfig.cs`
- Modify: `src/OpenClaw.Core/Models/Session.cs`
- Create: `src/OpenClaw.Tests/ToolDeclarationReductionConfigTests.cs`

**Interfaces:**
- Produces: `ToolingConfig.DeclarationReduction: ToolDeclarationReductionConfig`
- Produces: `ToolDeclarationReductionConfig` with properties `Enabled`, `Mode`, `MaxTools`, `MinTools`, `HardMaxTools`, `MinScore`, `FallbackToPresetOnEmpty`, `FallbackToRuleWhenSemanticUnavailable`, `EnablePromptDistillation`, `AlwaysIncludeTools`, and `NeverAutoIncludeTools`

- [ ] **Step 1: Write failing tests for config defaults and JSON roundtrip**

Create `src/OpenClaw.Tests/ToolDeclarationReductionConfigTests.cs`:

```csharp
using System.Text.Json;
using OpenClaw.Core.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolDeclarationReductionConfigTests
{
    [Fact]
    public void Defaults_AreBackwardCompatibleAndRuleReady()
    {
        var config = new GatewayConfig();

        Assert.False(config.Tooling.DeclarationReduction.Enabled);
        Assert.Equal("rule", config.Tooling.DeclarationReduction.Mode);
        Assert.Equal(16, config.Tooling.DeclarationReduction.MaxTools);
        Assert.Equal(4, config.Tooling.DeclarationReduction.MinTools);
        Assert.Equal(24, config.Tooling.DeclarationReduction.HardMaxTools);
        Assert.Equal(0.10, config.Tooling.DeclarationReduction.MinScore);
        Assert.True(config.Tooling.DeclarationReduction.FallbackToPresetOnEmpty);
        Assert.True(config.Tooling.DeclarationReduction.FallbackToRuleWhenSemanticUnavailable);
        Assert.False(config.Tooling.DeclarationReduction.EnablePromptDistillation);
        Assert.Empty(config.Tooling.DeclarationReduction.AlwaysIncludeTools);
        Assert.Empty(config.Tooling.DeclarationReduction.NeverAutoIncludeTools);
    }

    [Fact]
    public void GatewayConfigJson_RoundTripsDeclarationReduction()
    {
        var config = new GatewayConfig();
        config.Tooling.DeclarationReduction.Enabled = true;
        config.Tooling.DeclarationReduction.MaxTools = 12;
        config.Tooling.DeclarationReduction.AlwaysIncludeTools = ["read_file"];

        var json = JsonSerializer.Serialize(config, CoreJsonContext.Default.GatewayConfig);
        var roundTripped = JsonSerializer.Deserialize(json, CoreJsonContext.Default.GatewayConfig)!;

        Assert.True(roundTripped.Tooling.DeclarationReduction.Enabled);
        Assert.Equal(12, roundTripped.Tooling.DeclarationReduction.MaxTools);
        Assert.Equal(["read_file"], roundTripped.Tooling.DeclarationReduction.AlwaysIncludeTools);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~ToolDeclarationReductionConfigTests
```

Expected: build fails because `ToolingConfig.DeclarationReduction` and `ToolDeclarationReductionConfig` do not exist.

- [ ] **Step 3: Add config model**

Modify `src/OpenClaw.Core/Models/GatewayConfig.cs` inside `ToolingConfig`:

```csharp
public ToolDeclarationReductionConfig DeclarationReduction { get; set; } = new();
```

Add the new model near `ToolingConfig`:

```csharp
public sealed class ToolDeclarationReductionConfig
{
    public bool Enabled { get; set; } = false;
    public string Mode { get; set; } = "rule";
    public int MaxTools { get; set; } = 16;
    public int MinTools { get; set; } = 4;
    public int HardMaxTools { get; set; } = 24;
    public double MinScore { get; set; } = 0.10;
    public bool FallbackToPresetOnEmpty { get; set; } = true;
    public bool FallbackToRuleWhenSemanticUnavailable { get; set; } = true;
    public bool EnablePromptDistillation { get; set; } = false;
    public string[] AlwaysIncludeTools { get; set; } = [];
    public string[] NeverAutoIncludeTools { get; set; } = [];
}
```

Modify `src/OpenClaw.Core/Models/Session.cs` in the source-generation context near the existing `ToolingConfig` entry:

```csharp
[JsonSerializable(typeof(ToolDeclarationReductionConfig))]
```

- [ ] **Step 4: Run the focused tests and verify they pass**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~ToolDeclarationReductionConfigTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OpenClaw.Core/Models/GatewayConfig.cs src/OpenClaw.Core/Models/Session.cs src/OpenClaw.Tests/ToolDeclarationReductionConfigTests.cs
git commit -m "feat(core): add tool declaration reduction config"
```

---

### Task 2: Reducer Contracts

**Files:**
- Create: `src/OpenClaw.Core/Abstractions/IToolDeclarationReducer.cs`
- Create: `src/OpenClaw.Tests/ToolDeclarationReducerContractTests.cs`

**Interfaces:**
- Consumes: `ToolDeclarationReductionConfig`
- Produces: `IToolDeclarationReducer.ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)`
- Produces: `ToolDeclarationReductionRequest`, `ToolDeclarationReductionContext`, `ToolDeclarationReductionResult`, and `ToolDeclarationReductionDiagnostics`

- [ ] **Step 1: Write failing contract tests**

Create `src/OpenClaw.Tests/ToolDeclarationReducerContractTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class ToolDeclarationReducerContractTests
{
    [Fact]
    public void Request_DefaultsAreEmptyAndNotTurnRoutingProbe()
    {
        var request = new ToolDeclarationReductionRequest();

        Assert.Empty(request.RecentToolNames);
        Assert.Empty(request.RecentToolFailures);
        Assert.False(request.IsTurnRoutingProbe);
    }

    [Fact]
    public void Result_CarriesToolsAndDiagnostics()
    {
        using var schema = JsonDocument.Parse("""{"type":"object"}""");
        var tool = AIFunctionFactory.CreateDeclaration(
            "read_file",
            "Read a file",
            schema.RootElement.Clone(),
            returnJsonSchema: null);
        var diagnostics = new ToolDeclarationReductionDiagnostics
        {
            Enabled = true,
            Mode = "rule",
            CandidateCount = 5,
            SelectedCount = 1,
            MaxTools = 16,
            HardMaxTools = 24,
            PresetId = "coding",
            SelectedTools = ["read_file"]
        };

        var result = new ToolDeclarationReductionResult
        {
            Tools = [tool],
            Diagnostics = diagnostics
        };

        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Equal("rule", result.Diagnostics.Mode);
        Assert.Equal(["read_file"], result.Diagnostics.SelectedTools);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~ToolDeclarationReducerContractTests
```

Expected: build fails because the reducer contract types do not exist.

- [ ] **Step 3: Add reducer contracts**

Create `src/OpenClaw.Core/Abstractions/IToolDeclarationReducer.cs`:

```csharp
using Microsoft.Extensions.AI;
using OpenClaw.Core.Models;

namespace OpenClaw.Core.Abstractions;

public interface IToolDeclarationReducer
{
    ValueTask<ToolDeclarationReductionResult> ReduceAsync(
        ToolDeclarationReductionContext context,
        CancellationToken ct);
}

public sealed class ToolDeclarationReductionRequest
{
    public IReadOnlyList<string> RecentToolNames { get; init; } = [];
    public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public bool IsTurnRoutingProbe { get; init; }
}

public sealed class ToolDeclarationReductionContext
{
    public required Session Session { get; init; }
    public string? UserMessage { get; init; }
    public required IReadOnlyList<AITool> CandidateTools { get; init; }
    public ResolvedToolPreset? Preset { get; init; }
    public required ToolDeclarationReductionConfig Config { get; init; }
    public IReadOnlyList<string> RecentToolNames { get; init; } = [];
    public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public bool IsTurnRoutingProbe { get; init; }
}

public sealed class ToolDeclarationReductionResult
{
    public required IReadOnlyList<AITool> Tools { get; init; }
    public required ToolDeclarationReductionDiagnostics Diagnostics { get; init; }
}

public sealed class ToolDeclarationReductionDiagnostics
{
    public bool Enabled { get; init; }
    public string Mode { get; init; } = "off";
    public int CandidateCount { get; init; }
    public int SelectedCount { get; init; }
    public int MaxTools { get; init; }
    public int HardMaxTools { get; init; }
    public string? PresetId { get; init; }
    public bool FallbackUsed { get; init; }
    public string? FallbackReason { get; init; }
    public IReadOnlyList<string> SelectedTools { get; init; } = [];
    public IReadOnlyList<string> PinnedTools { get; init; } = [];
    public IReadOnlyList<string> SkippedPinnedTools { get; init; } = [];
    public IReadOnlyDictionary<string, double> Scores { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);
}
```

- [ ] **Step 4: Run the focused tests and verify they pass**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~ToolDeclarationReducerContractTests
```

Expected: tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OpenClaw.Core/Abstractions/IToolDeclarationReducer.cs src/OpenClaw.Tests/ToolDeclarationReducerContractTests.cs
git commit -m "feat(core): add tool declaration reducer contracts"
```

---

### Task 3: Rule-Based Reducer

**Files:**
- Create: `src/OpenClaw.Agent/ToolDeclarations/ToolDeclarationText.cs`
- Create: `src/OpenClaw.Agent/ToolDeclarations/RuleBasedToolDeclarationReducer.cs`
- Create: `src/OpenClaw.Tests/RuleBasedToolDeclarationReducerTests.cs`

**Interfaces:**
- Consumes: `IToolDeclarationReducer`, `ToolDeclarationReductionContext`, and `ToolDeclarationReductionConfig`
- Produces: `RuleBasedToolDeclarationReducer : IToolDeclarationReducer`

- [ ] **Step 1: Write failing reducer tests**

Create `src/OpenClaw.Tests/RuleBasedToolDeclarationReducerTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using OpenClaw.Agent.ToolDeclarations;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class RuleBasedToolDeclarationReducerTests
{
    [Fact]
    public async Task ReduceAsync_RanksExplicitToolNameFirst()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("read_file", "Read a file from disk", "path"),
            Tool("message", "Send a message", "text"),
            Tool("browser", "Open a web page", "url")
        };

        var result = await reducer.ReduceAsync(Context(tools, "please use read_file on this path", maxTools: 2), TestContext.Current.CancellationToken);

        Assert.Equal(["read_file", "browser"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(3, result.Diagnostics.CandidateCount);
        Assert.Equal(2, result.Diagnostics.SelectedCount);
        Assert.True(result.Diagnostics.Scores["read_file"] > result.Diagnostics.Scores["browser"]);
    }

    [Fact]
    public async Task ReduceAsync_UsesParameterNamesForGenericTools()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = new[]
        {
            Tool("message", "Send content to a channel", "chatId text"),
            Tool("gateway", "Manage gateway runtime", "operation"),
            Tool("sessions", "Inspect active sessions", "sessionId")
        };

        var result = await reducer.ReduceAsync(Context(tools, "send text to chatId", maxTools: 1), TestContext.Current.CancellationToken);

        Assert.Equal(["message"], result.Tools.Select(static item => item.Name).ToArray());
    }

    [Fact]
    public async Task ReduceAsync_AlwaysIncludeCannotExceedHardMax()
    {
        var reducer = new RuleBasedToolDeclarationReducer();
        var tools = Enumerable.Range(1, 10)
            .Select(index => Tool($"tool_{index}", $"Tool {index}", "value"))
            .ToArray();
        var context = Context(tools, "tool 1", maxTools: 2, hardMaxTools: 3);
        context.Config.AlwaysIncludeTools = ["tool_1", "tool_2", "tool_3", "tool_4"];

        var result = await reducer.ReduceAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Tools.Count);
        Assert.Equal(["tool_1", "tool_2", "tool_3"], result.Tools.Select(static item => item.Name).ToArray());
        Assert.Equal(["tool_4"], result.Diagnostics.SkippedPinnedTools);
    }

    private static ToolDeclarationReductionContext Context(IReadOnlyList<AITool> tools, string prompt, int maxTools, int hardMaxTools = 24)
    {
        return new ToolDeclarationReductionContext
        {
            Session = new Session { Id = "sess1", ChannelId = "websocket", SenderId = "user1" },
            UserMessage = prompt,
            CandidateTools = tools,
            Config = new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = "rule",
                MaxTools = maxTools,
                MinTools = 1,
                HardMaxTools = hardMaxTools,
                MinScore = 0.0
            }
        };
    }

    private static AITool Tool(string name, string description, string parameterNames)
    {
        var properties = string.Join(",", parameterNames.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static name => $"\"{name}\":{{\"type\":\"string\"}}"));
        using var schema = JsonDocument.Parse($$"{"type":"object","properties":{ {{{properties}}} }}");
        return AIFunctionFactory.CreateDeclaration(
            name,
            description,
            schema.RootElement.Clone(),
            returnJsonSchema: null);
    }
}
```

- [ ] **Step 2: Run reducer tests and verify they fail**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

Expected: build fails because `RuleBasedToolDeclarationReducer` does not exist.

- [ ] **Step 3: Add searchable text helper**

Create `src/OpenClaw.Agent/ToolDeclarations/ToolDeclarationText.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.AI;

namespace OpenClaw.Agent.ToolDeclarations;

internal static class ToolDeclarationText
{
    public static string Build(AITool tool)
    {
        var builder = new StringBuilder();
        builder.Append(tool.Name);
        builder.Append(' ');
        builder.Append(tool.Description);
        var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
        if (declaration?.JsonSchema is not null)
        {
            builder.Append(' ');
            builder.Append(declaration.JsonSchema.ToString());
        }

        return builder.ToString();
    }
}
```

- [ ] **Step 4: Add rule-based reducer**

Create `src/OpenClaw.Agent/ToolDeclarations/RuleBasedToolDeclarationReducer.cs`:

```csharp
using Microsoft.Extensions.AI;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Agent.ToolDeclarations;

public sealed class RuleBasedToolDeclarationReducer : IToolDeclarationReducer
{
    private static readonly string[] HighRiskTools = ["shell", "process", "write_file", "code_exec"];

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = context.Config;
        var hardMax = Math.Max(1, config.HardMaxTools);
        var maxTools = Math.Clamp(config.MaxTools, 1, hardMax);
        var minTools = Math.Clamp(config.MinTools, 0, maxTools);
        var promptTokens = Tokenize(context.UserMessage ?? string.Empty).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidateByName = context.CandidateTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        var selected = new List<AITool>();
        var pinned = new List<string>();
        var skippedPinned = new List<string>();

        foreach (var requested in config.AlwaysIncludeTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            if (selected.Count >= hardMax)
            {
                skippedPinned.Add(requested);
                continue;
            }

            if (candidateByName.TryGetValue(requested.Trim(), out var tool) && selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            {
                selected.Add(tool);
                pinned.Add(tool.Name);
            }
            else
            {
                skippedPinned.Add(requested.Trim());
            }
        }

        var scores = context.CandidateTools
            .Where(tool => selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            .Select(tool => new ToolScore(tool, Score(tool, promptTokens, context)))
            .Where(item => item.Score >= config.MinScore || selected.Count + minTools > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
            .ToArray();

        foreach (var item in scores)
        {
            if (selected.Count >= maxTools)
                break;
            selected.Add(item.Tool);
        }

        foreach (var item in scores)
        {
            if (selected.Count >= minTools || selected.Count >= maxTools)
                break;
            if (selected.All(existing => !string.Equals(existing.Name, item.Tool.Name, StringComparison.Ordinal)))
                selected.Add(item.Tool);
        }

        var scoreMap = scores.ToDictionary(static item => item.Tool.Name, static item => item.Score, StringComparer.Ordinal);
        foreach (var tool in selected)
            scoreMap.TryAdd(tool.Name, pinned.Contains(tool.Name, StringComparer.Ordinal) ? 1.0 : 0.0);

        var diagnostics = new ToolDeclarationReductionDiagnostics
        {
            Enabled = true,
            Mode = "rule",
            CandidateCount = context.CandidateTools.Count,
            SelectedCount = selected.Count,
            MaxTools = maxTools,
            HardMaxTools = hardMax,
            PresetId = context.Preset?.PresetId,
            SelectedTools = selected.Select(static tool => tool.Name).ToArray(),
            PinnedTools = pinned,
            SkippedPinnedTools = skippedPinned,
            Scores = scoreMap
        };

        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = diagnostics
        });
    }

    private static double Score(AITool tool, HashSet<string> promptTokens, ToolDeclarationReductionContext context)
    {
        var score = 0.0;
        if (promptTokens.Contains(tool.Name))
            score += 1.0;

        var textTokens = Tokenize(ToolDeclarationText.Build(tool)).ToArray();
        score += textTokens.Count(promptTokens.Contains) * 0.12;

        if (context.RecentToolNames.Contains(tool.Name, StringComparer.Ordinal))
            score += 0.20;

        if (context.RecentToolFailures.TryGetValue(tool.Name, out var failures))
            score -= Math.Min(0.30, failures * 0.10);

        if (HighRiskTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase) && !promptTokens.Contains(tool.Name))
            score -= 0.08;

        return Math.Clamp(score, 0.0, 1.0);
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isTokenChar = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isTokenChar && start < 0)
                start = i;
            else if (!isTokenChar && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    private sealed record ToolScore(AITool Tool, double Score);
}
```

- [ ] **Step 5: Run reducer tests and fix compile issues in the touched files**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

Expected: tests pass. If `BinaryData` or `AIFunctionDeclaration.JsonSchema` APIs differ from the snippets, adapt only the helper and tests to the package version already referenced by the repo.

- [ ] **Step 6: Commit**

```powershell
git add src/OpenClaw.Agent/ToolDeclarations/ToolDeclarationText.cs src/OpenClaw.Agent/ToolDeclarations/RuleBasedToolDeclarationReducer.cs src/OpenClaw.Tests/RuleBasedToolDeclarationReducerTests.cs
git commit -m "feat(agent): add rule-based tool declaration reducer"
```

---

### Task 4: OpenClawToolExecutor Integration

**Files:**
- Modify: `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
- Modify: `src/OpenClaw.Tests/OpenClawToolExecutorTests.cs`

**Interfaces:**
- Consumes: `IToolDeclarationReducer`, `ToolDeclarationReductionRequest`, `ToolDeclarationReductionContext`, `ToolDeclarationReductionResult`
- Produces: `OpenClawToolExecutor.GetToolDeclarations(Session session, string? userMessage, ToolDeclarationReductionRequest? request = null)`

- [ ] **Step 1: Add failing executor tests for disabled mode, reduction, and fail-open**

Append tests to `src/OpenClaw.Tests/OpenClawToolExecutorTests.cs`:

```csharp
[Fact]
public void GetToolDeclarations_WhenReductionDisabled_ReturnsPresetAllowedDeclarations()
{
    var executor = CreateExecutor([new RecordingTool("read_file", "ok"), new RecordingTool("shell", "ok")]);
    var session = CreateSession();

    var tools = executor.GetToolDeclarations(session, "read a file");

    Assert.Equal(["read_file", "shell"], tools.Select(static item => item.Name).ToArray());
}

[Fact]
public void GetToolDeclarations_WhenReductionEnabled_UsesReducer()
{
    var reducer = new FixedToolDeclarationReducer(["read_file"]);
    var config = new GatewayConfig();
    config.Tooling.DeclarationReduction.Enabled = true;
    var executor = CreateExecutor(
        [new RecordingTool("read_file", "ok"), new RecordingTool("shell", "ok")],
        config: config,
        toolDeclarationReducer: reducer);

    var tools = executor.GetToolDeclarations(CreateSession(), "read a file");

    Assert.Equal(["read_file"], tools.Select(static item => item.Name).ToArray());
    Assert.Equal("read a file", reducer.LastContext?.UserMessage);
}

[Fact]
public void GetToolDeclarations_WhenReducerThrows_FailsOpenToPresetAllowedTools()
{
    var config = new GatewayConfig();
    config.Tooling.DeclarationReduction.Enabled = true;
    var executor = CreateExecutor(
        [new RecordingTool("read_file", "ok"), new RecordingTool("shell", "ok")],
        config: config,
        toolDeclarationReducer: new ThrowingToolDeclarationReducer());

    var tools = executor.GetToolDeclarations(CreateSession(), "read a file");

    Assert.Equal(["read_file", "shell"], tools.Select(static item => item.Name).ToArray());
}

private sealed class FixedToolDeclarationReducer(string[] selectedNames) : IToolDeclarationReducer
{
    public ToolDeclarationReductionContext? LastContext { get; private set; }

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
    {
        LastContext = context;
        var selected = context.CandidateTools.Where(tool => selectedNames.Contains(tool.Name, StringComparer.Ordinal)).ToArray();
        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = new ToolDeclarationReductionDiagnostics
            {
                Enabled = true,
                Mode = "test",
                CandidateCount = context.CandidateTools.Count,
                SelectedCount = selected.Length,
                MaxTools = context.Config.MaxTools,
                HardMaxTools = context.Config.HardMaxTools,
                SelectedTools = selected.Select(static tool => tool.Name).ToArray()
            }
        });
    }
}

private sealed class ThrowingToolDeclarationReducer : IToolDeclarationReducer
{
    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
        => throw new InvalidOperationException("reducer failed");
}
```

Update the existing `CreateExecutor` helper in the same file to accept a new optional argument:

```csharp
IToolDeclarationReducer? toolDeclarationReducer = null
```

Pass that value into the `OpenClawToolExecutor` constructor.

- [ ] **Step 2: Run executor tests and verify they fail**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests.GetToolDeclarations"
```

Expected: build fails because the constructor parameter and reduction-aware overload do not exist.

- [ ] **Step 3: Add reducer dependency and overload**

Modify `src/OpenClaw.Agent/OpenClawToolExecutor.cs`:

```csharp
private readonly IToolDeclarationReducer? _toolDeclarationReducer;
```

Add constructor parameter after `toolPresetResolver`:

```csharp
IToolDeclarationReducer? toolDeclarationReducer = null,
```

Assign it:

```csharp
_toolDeclarationReducer = toolDeclarationReducer;
```

Keep the current method as a compatibility wrapper:

```csharp
public IList<AITool> GetToolDeclarations(Session session)
    => GetToolDeclarations(session, userMessage: null, request: null);
```

Add the richer overload:

```csharp
public IList<AITool> GetToolDeclarations(
    Session session,
    string? userMessage,
    ToolDeclarationReductionRequest? request = null)
{
    if (session.RouteToolsDisabled)
        return [];

    var preset = _toolPresetResolver?.Resolve(session, _toolsByName.Keys);
    var candidates = _toolDeclarations
        .Where(item => IsToolAllowedForSession(session, item.Name, preset))
        .ToArray();

    var reductionConfig = _config.Tooling.DeclarationReduction;
    if (!reductionConfig.Enabled || string.Equals(reductionConfig.Mode, "off", StringComparison.OrdinalIgnoreCase) || _toolDeclarationReducer is null)
        return candidates;

    try
    {
        var reduction = _toolDeclarationReducer.ReduceAsync(new ToolDeclarationReductionContext
        {
            Session = session,
            UserMessage = userMessage,
            CandidateTools = candidates,
            Preset = preset,
            Config = reductionConfig,
            RecentToolNames = request?.RecentToolNames ?? [],
            RecentToolFailures = request?.RecentToolFailures ?? new Dictionary<string, int>(StringComparer.Ordinal),
            IsTurnRoutingProbe = request?.IsTurnRoutingProbe ?? false
        }, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        if (reduction.Tools.Count > 0 || !reductionConfig.FallbackToPresetOnEmpty)
            return reduction.Tools.ToArray();

        _logger?.LogWarning("Tool declaration reduction returned no tools; falling back to {CandidateCount} preset-allowed tools.", candidates.Length);
        return candidates;
    }
    catch (Exception ex)
    {
        _logger?.LogWarning(ex, "Tool declaration reduction failed; falling back to {CandidateCount} preset-allowed tools.", candidates.Length);
        return candidates;
    }
}
```

The synchronous wait is acceptable because both planned reducers are CPU-only in this implementation and the existing `GetToolDeclarations` surface is synchronous. If a later semantic plugin uses external embedding services or model calls, introduce an async runtime call path instead of blocking here.

- [ ] **Step 4: Run executor tests and verify they pass**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests.GetToolDeclarations"
```

Expected: tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/OpenClaw.Agent/OpenClawToolExecutor.cs src/OpenClaw.Tests/OpenClawToolExecutorTests.cs
git commit -m "feat(agent): reduce tool declarations in executor"
```

---

### Task 5: Runtime Wiring for Native and MAF

**Files:**
- Modify: `src/OpenClaw.Agent/AgentRuntime.cs`
- Modify: `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- Modify: `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`
- Modify: `src/OpenClaw.Tests/MafAdapterTests.cs`

**Interfaces:**
- Consumes: `OpenClawToolExecutor.GetToolDeclarations(Session, string?, ToolDeclarationReductionRequest?)`
- Produces: native and MAF parity where current user message reaches the shared reducer path

- [ ] **Step 1: Write failing MAF parity test**

Add a new test near `MafAgentRuntime_FiltersToolsByPresetResolver` in `src/OpenClaw.Tests/MafAdapterTests.cs`:

```csharp
[Fact]
public async Task MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall()
{
    var services = new ServiceCollection()
        .AddSingleton<IToolDeclarationReducer>(new AllowOnlyDeclarationReducer("echo_tool"))
        .BuildServiceProvider();
    var executionService = new CapturingLlmExecutionService();
    var storagePath = Path.Join(Path.GetTempPath(), "openclaw-maf-declaration-reduction-tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(storagePath);

    try
    {
        var runtime = new MafAgentRuntime(
            new AgentRuntimeFactoryContext
            {
                Services = services,
                Config = new GatewayConfig
                {
                    Tooling = new ToolingConfig
                    {
                        DeclarationReduction = new ToolDeclarationReductionConfig
                        {
                            Enabled = true,
                            Mode = "rule",
                            MaxTools = 1,
                            HardMaxTools = 1
                        }
                    },
                    Memory = new MemoryConfig { StoragePath = storagePath },
                    Llm = new LlmProviderConfig { Provider = "test-maf", Model = "maf-test-model" }
                },
                RuntimeState = new GatewayRuntimeState
                {
                    RequestedMode = "jit",
                    EffectiveMode = GatewayRuntimeMode.Jit,
                    DynamicCodeSupported = true
                },
                ChatClient = new MafTestChatClient(),
                Tools = [new TestTool("echo_tool"), new TestTool("shell")],
                MemoryStore = new FileMemoryStore(storagePath, 4),
                RuntimeMetrics = new RuntimeMetrics(),
                ProviderUsage = new ProviderUsageTracker(),
                LlmExecutionService = executionService,
                Skills = [],
                SkillsConfig = new SkillsConfig(),
                WorkspacePath = null,
                PluginSkillDirs = [],
                Logger = NullLogger.Instance,
                Hooks = [],
                RequireToolApproval = false,
                ApprovalRequiredTools = [],
                IsContractTokenBudgetExceeded = null,
                IsContractRuntimeBudgetExceeded = null,
                RecordContractTurnUsage = null,
                AppendContractSnapshot = null
            },
            new MafOptions(),
            new MafAgentFactory(Options.Create(new MafOptions()), NullLoggerFactory.Instance, services),
            new MafSessionStateStore(
                new GatewayConfig { Memory = new MemoryConfig { StoragePath = storagePath } },
                Options.Create(new MafOptions()),
                NullLogger<MafSessionStateStore>.Instance),
            new MafTelemetryAdapter(),
            NullLogger<MafAgentRuntime>.Instance);

        await runtime.RunAsync(CreateSession("maf-declaration-reduction"), "use echo tool", TestContext.Current.CancellationToken);

        Assert.Equal(["echo_tool"], executionService.LastToolNames);
    }
    finally
    {
        Directory.Delete(storagePath, recursive: true);
    }
}

private sealed class AllowOnlyDeclarationReducer(string toolName) : IToolDeclarationReducer
{
    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
    {
        var tools = context.CandidateTools.Where(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)).ToArray();
        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = tools,
            Diagnostics = new ToolDeclarationReductionDiagnostics
            {
                Enabled = true,
                Mode = "test",
                CandidateCount = context.CandidateTools.Count,
                SelectedCount = tools.Length,
                MaxTools = context.Config.MaxTools,
                HardMaxTools = context.Config.HardMaxTools,
                SelectedTools = tools.Select(static tool => tool.Name).ToArray()
            }
        });
    }
}
```

- [ ] **Step 2: Run MAF parity test and verify it fails**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall
```

Expected: test fails because `MafAgentRuntime` does not pass `IToolDeclarationReducer` into `OpenClawToolExecutor` yet.

- [ ] **Step 3: Register reducer in DI**

Modify `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs` near the tool preset registration:

```csharp
services.AddSingleton<IToolDeclarationReducer, RuleBasedToolDeclarationReducer>();
```

Add the required namespace:

```csharp
using OpenClaw.Agent.ToolDeclarations;
```

- [ ] **Step 4: Pass reducer into native runtime construction**

Modify `src/OpenClaw.Agent/AgentRuntime.cs` constructor signature to accept:

```csharp
IToolDeclarationReducer? toolDeclarationReducer = null,
```

Pass it to `OpenClawToolExecutor`:

```csharp
toolDeclarationReducer: toolDeclarationReducer,
```

Modify `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs` inside `CreateRuntime`:

```csharp
toolDeclarationReducer: context.Services.GetService(typeof(OpenClaw.Core.Abstractions.IToolDeclarationReducer)) as OpenClaw.Core.Abstractions.IToolDeclarationReducer,
```

- [ ] **Step 5: Use reduction-aware tool declarations in native runtime calls**

In `src/OpenClaw.Agent/AgentRuntime.cs`, replace the model-call tool assignment patterns:

```csharp
Tools = _toolExecutor.GetToolDeclarations(session),
```

with:

```csharp
Tools = _toolExecutor.GetToolDeclarations(session, userMessage),
```

In `ApplyTurnRoutingAsync`, replace:

```csharp
Tools = _toolExecutor.GetToolDeclarations(session),
```

with:

```csharp
Tools = _toolExecutor.GetToolDeclarations(
    session,
    userMessage,
    new ToolDeclarationReductionRequest { IsTurnRoutingProbe = true }),
```

- [ ] **Step 6: Pass reducer into MAF runtime and use the richer overload**

Modify `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs` constructor call to `OpenClawToolExecutor`:

```csharp
toolDeclarationReducer: context.Services.GetService(typeof(IToolDeclarationReducer)) as IToolDeclarationReducer,
```

Modify `CreateAgent`:

```csharp
var tools = _toolExecutor.GetToolDeclarations(session, userMessage)
    .Select(tool => _mafToolsByName[tool.Name])
    .ToArray();
```

Modify `ApplyTurnRoutingAsync`:

```csharp
baseOptions.Tools = _toolExecutor.GetToolDeclarations(
    session,
    userMessage,
    new ToolDeclarationReductionRequest { IsTurnRoutingProbe = true });
```

- [ ] **Step 7: Run runtime parity tests**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_FiltersToolsByPresetResolver"
```

Expected: tests pass and MAF still respects preset filtering.

- [ ] **Step 8: Commit**

```powershell
git add src/OpenClaw.Agent/AgentRuntime.cs src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs src/OpenClaw.Tests/MafAdapterTests.cs
git commit -m "feat(runtime): share declaration reduction across native and maf"
```

---

### Task 6: Self-Contained Semantic Reducer Plugin

**Files:**
- Modify: `src/OpenClaw.PluginKit/INativeDynamicPlugin.cs`
- Modify: `src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs`
- Modify: `src/OpenClaw.Agent/IAgentRuntimeFactory.cs`
- Modify: `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs`
- Modify: `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntimeFactory.cs`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`
- Modify: `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/OpenClaw.Plugins.ToolDeclarationReduction.Semantic.csproj`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/openclaw.native-plugin.json`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/SemanticToolDeclarationReductionPlugin.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/SemanticToolDeclarationReducer.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/SemanticToolIndex.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/PromptIntentDistiller.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/TextEmbedding/HashingTextVectorizer.cs`
- Create: `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/TextEmbedding/CosineSimilarity.cs`
- Create: `src/OpenClaw.Tests/SemanticToolDeclarationReducerTests.cs`
- Create: `src/OpenClaw.Tests/NativeDynamicToolDeclarationReducerPluginTests.cs`

**Interfaces:**
- Consumes: `IToolDeclarationReducer`, `ToolDeclarationReductionContext`, `ToolDeclarationReductionConfig`
- Produces: native dynamic plugin registration for tool declaration reducers
- Produces: OpenClaw-owned `SemanticToolDeclarationReducer : IToolDeclarationReducer`
- Produces: semantic/hybrid mode behavior that borrows ElBruno's architecture but does not reference ElBruno packages or copy ElBruno source files

- [ ] **Step 1: Write failing plugin registration tests**

Create `src/OpenClaw.Tests/NativeDynamicToolDeclarationReducerPluginTests.cs` with focused tests for the new plugin registration surface:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Agent.Plugins;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.PluginKit;
using Xunit;

namespace OpenClaw.Tests;

public sealed class NativeDynamicToolDeclarationReducerPluginTests
{
    [Fact]
    public void PluginContext_CanRegisterToolDeclarationReducer()
    {
        var context = NativeDynamicPluginHost.CreateTestRegistrationContext("semantic-reducer", NullLogger.Instance);
        var reducer = new PassThroughReducer();

        context.RegisterToolDeclarationReducer(reducer);

        Assert.Same(reducer, context.ToolDeclarationReducers.Single());
    }

    private sealed class PassThroughReducer : IToolDeclarationReducer
    {
        public ValueTask<ToolDeclarationReductionResult> ReduceAsync(ToolDeclarationReductionContext context, CancellationToken ct)
            => ValueTask.FromResult(new ToolDeclarationReductionResult
            {
                Tools = context.CandidateTools,
                Diagnostics = new ToolDeclarationReductionDiagnostics
                {
                    Enabled = true,
                    Mode = "test",
                    CandidateCount = context.CandidateTools.Count,
                    SelectedCount = context.CandidateTools.Count,
                    MaxTools = context.Config.MaxTools,
                    HardMaxTools = context.Config.HardMaxTools,
                    SelectedTools = context.CandidateTools.Select(static tool => tool.Name).ToArray()
                }
            });
    }
}
```

Add `internal static INativeDynamicPluginContext CreateTestRegistrationContext(string pluginId, ILogger logger)` on `NativeDynamicPluginHost` and rely on the existing `InternalsVisibleTo` test access. Keep `RegistrationContext` private.

- [ ] **Step 2: Add reducer registration to PluginKit and native plugin host**

Modify `src/OpenClaw.PluginKit/INativeDynamicPlugin.cs`:

```csharp
void RegisterToolDeclarationReducer(IToolDeclarationReducer reducer);
```

Modify `src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs`:

```csharp
private readonly List<IToolDeclarationReducer> _toolDeclarationReducers = [];
public IReadOnlyList<IToolDeclarationReducer> ToolDeclarationReducers => _toolDeclarationReducers;
```

Clear the list in `LoadAsync` and `DisposeAsync`, snapshot it in `LoadPluginAsync`, add registered reducers to the host after successful plugin registration, and truncate it on load failure just like tools, hooks, and interceptors.

Extend `RegistrationContext`:

```csharp
public List<IToolDeclarationReducer> ToolDeclarationReducers { get; } = [];

public void RegisterToolDeclarationReducer(IToolDeclarationReducer reducer)
{
    ToolDeclarationReducers.Add(reducer);
    Capabilities.Add(PluginCapabilityPolicy.Hooks);
}
```

Reuse `PluginCapabilityPolicy.Hooks` for declaration reducers in this first implementation because reducers observe and transform tool declaration exposure but do not execute tools or register providers.

- [ ] **Step 3: Make runtime construction accept the selected effective reducer**

Modify `src/OpenClaw.Agent/IAgentRuntimeFactory.cs`:

```csharp
public IToolDeclarationReducer? ToolDeclarationReducer { get; init; }
```

Modify native and MAF factories to prefer the explicit context reducer over DI lookup:

```csharp
toolDeclarationReducer: context.ToolDeclarationReducer
    ?? context.Services.GetService(typeof(IToolDeclarationReducer)) as IToolDeclarationReducer,
```

This keeps the rule reducer path working while allowing runtime composition to choose a semantic plugin reducer when `Mode=semantic` or `Mode=hybrid`.

- [ ] **Step 4: Select rule vs semantic reducer during runtime composition**

In `src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs`, after dynamic native plugins are loaded and before `CreateAgentRuntime`, compute the effective reducer:

```csharp
var effectiveToolDeclarationReducer = SelectToolDeclarationReducer(
    config.Tooling.DeclarationReduction,
    app.Services.GetService<IToolDeclarationReducer>(),
    pluginComposition.NativeDynamicPluginHost?.ToolDeclarationReducers ?? []);
```

Recommended selector behavior:

- `Enabled=false` or `Mode=off`: return the rule reducer or `null`; executor will still skip reduction while disabled.
- `Mode=rule`: return the rule reducer.
- `Mode=semantic` or `Mode=hybrid`: return the first plugin reducer when present; otherwise return the rule reducer if `FallbackToRuleWhenSemanticUnavailable=true`, else return `null`.
- Log a warning when semantic/hybrid was requested but no semantic reducer plugin was registered.

Pass `effectiveToolDeclarationReducer` into `CreateAgentRuntime`, then into `AgentRuntimeFactoryContext.ToolDeclarationReducer`.

- [ ] **Step 5: Run plugin registration tests**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~NativeDynamicToolDeclarationReducerPluginTests
```

Expected: tests pass.

- [ ] **Step 6: Create the self-contained semantic plugin project**

Create `src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/OpenClaw.Plugins.ToolDeclarationReduction.Semantic.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>OpenClaw.Plugins.ToolDeclarationReduction.Semantic</RootNamespace>
    <IsAotCompatible>false</IsAotCompatible>
    <IsTrimmable>false</IsTrimmable>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <NoWarn>$(NoWarn);1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\OpenClaw.Core\OpenClaw.Core.csproj" />
    <ProjectReference Include="..\OpenClaw.PluginKit\OpenClaw.PluginKit.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="OpenClaw.Tests" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="openclaw.native-plugin.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

Do not add `PackageReference` entries for `ElBruno.*`, `ModelContextProtocol.MCPToolRouter`, `ElBruno.LocalEmbeddings`, or `ElBruno.LocalLLMs`. The first implementation uses OpenClaw-owned deterministic vectorization so the plugin remains self-contained.

Create `openclaw.native-plugin.json`:

```json
{
  "id": "tool-declaration-reduction-semantic",
  "type": "OpenClaw.Plugins.ToolDeclarationReduction.Semantic.SemanticToolDeclarationReductionPlugin, OpenClaw.Plugins.ToolDeclarationReduction.Semantic",
  "capabilities": ["hooks"],
  "description": "Self-contained semantic/hybrid tool declaration reducer."
}
```

Use the existing `hooks` capability for this first implementation.

- [ ] **Step 7: Add the semantic plugin entry point**

Create `SemanticToolDeclarationReductionPlugin.cs`:

```csharp
using OpenClaw.PluginKit;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

public sealed class SemanticToolDeclarationReductionPlugin : INativeDynamicPlugin
{
    public void Register(INativeDynamicPluginContext context)
    {
        context.RegisterToolDeclarationReducer(new SemanticToolDeclarationReducer(context.Logger));
    }
}
```

The first implementation reads no plugin-local configuration; all behavior comes from `ToolDeclarationReductionConfig` on the reducer context.

- [ ] **Step 8: Add self-contained vectorization and semantic index**

Create `TextEmbedding/HashingTextVectorizer.cs`:

```csharp
namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

internal sealed class HashingTextVectorizer
{
    public const int DefaultDimensions = 512;

    public float[] Vectorize(string text, int dimensions = DefaultDimensions)
    {
        var vector = new float[dimensions];
        foreach (var token in Tokenize(text))
        {
            var bucket = Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(token)) % dimensions;
            vector[bucket] += 1f;
        }

        Normalize(vector);
        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        foreach (var word in ReadWords(text))
        {
            yield return word;

            foreach (var part in word.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.Equals(part, word, StringComparison.OrdinalIgnoreCase))
                    yield return part;
            }

            if (word.Length >= 4)
            {
                for (var i = 0; i <= word.Length - 3; i++)
                    yield return "ng:" + word.Substring(i, 3);
            }
        }
    }

    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        foreach (var value in vector)
            sum += value * value;

        if (sum <= 0)
            return;

        var length = Math.Sqrt(sum);
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / length);
    }

    private static IEnumerable<string> ReadWords(string text)
    {
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWord = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }
}
```

Create `TextEmbedding/CosineSimilarity.cs` with a small static dot-product helper over normalized vectors:

```csharp
namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

internal static class CosineSimilarity
{
    public static double Score(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var score = 0.0;
        for (var i = 0; i < count; i++)
            score += left[i] * right[i];
        return Math.Clamp(score, 0.0, 1.0);
    }
}
```

Create `SemanticToolIndex.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using OpenClaw.Plugins.ToolDeclarationReduction.Semantic.TextEmbedding;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

internal sealed class SemanticToolIndex
{
    private readonly HashingTextVectorizer _vectorizer;
    private readonly Entry[] _entries;

    public string Fingerprint { get; }

    private SemanticToolIndex(string fingerprint, HashingTextVectorizer vectorizer, Entry[] entries)
    {
        Fingerprint = fingerprint;
        _vectorizer = vectorizer;
        _entries = entries;
    }

    public static SemanticToolIndex Build(IReadOnlyList<AITool> tools)
    {
        var vectorizer = new HashingTextVectorizer();
        var entries = tools
            .Select(tool =>
            {
                var text = BuildIndexText(tool);
                return new Entry(tool, text, vectorizer.Vectorize(text));
            })
            .ToArray();

        return new SemanticToolIndex(BuildFingerprint(entries), vectorizer, entries);
    }

    public IReadOnlyList<SemanticToolSearchResult> Search(string query, int topK, double minScore)
    {
        if (string.IsNullOrWhiteSpace(query) || topK <= 0)
            return [];

        var queryVector = _vectorizer.Vectorize(query);
        return _entries
            .Select(entry => new SemanticToolSearchResult(entry.Tool, CosineSimilarity.Score(queryVector, entry.Vector)))
            .Where(result => result.Score >= minScore)
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Tool.Name, StringComparer.Ordinal)
            .Take(topK)
            .ToArray();
    }

    private static string BuildIndexText(AITool tool)
    {
        var declaration = tool as AIFunctionDeclaration ?? tool.GetService<AIFunctionDeclaration>();
        var schema = declaration?.JsonSchema?.ToString() ?? string.Empty;
        return string.Concat(tool.Name, ": ", tool.Description, ". Parameters: ", schema);
    }

    private static string BuildFingerprint(IReadOnlyList<Entry> entries)
    {
        var builder = new StringBuilder();
        foreach (var entry in entries.OrderBy(static item => item.Tool.Name, StringComparer.Ordinal))
        {
            builder.Append(entry.Tool.Name);
            builder.Append('\0');
            builder.Append(entry.Text);
            builder.Append('\0');
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes);
    }

    private sealed record Entry(AITool Tool, string Text, float[] Vector);
}

internal sealed record SemanticToolSearchResult(AITool Tool, double Score);
```

Index text should be equivalent in spirit to ElBruno's `{Name}: {Description}. Parameters: {Parameters}` template, but implemented with OpenClaw code:

```plaintext
tool name + description + parameter names + raw JSON schema text
```

The index should cache by a stable fingerprint of candidate tool names and declaration text. Rebuild only when the candidate set changes.

- [ ] **Step 9: Add prompt intent distillation without external LLM dependencies**

Create `PromptIntentDistiller.cs`:

```csharp
namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

internal static class PromptIntentDistiller
{
    public static IReadOnlyList<string> DistillActionPhrases(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return [];

        var phrases = prompt
            .Split(['.', ';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(static part => part.Split([" and ", " then ", " also ", " plus "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(static part => part.Trim())
            .Where(static part => part.Length >= 3)
            .Select(static part => part.Length > 96 ? part[..96] : part)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return phrases.Length > 0 ? phrases : [prompt.Trim()];
    }
}
```

This is not an LLM call in the first implementation. It borrows the ElBruno idea of turning a long prompt into multiple action phrases, but uses deterministic extraction:

- split on sentence boundaries and coordination words;
- keep verb/object and noun phrases that contain technical terms;
- preserve explicit tool names and schema-like identifiers such as `path`, `url`, `query`, `chatId`, `command`;
- cap phrase count to avoid query explosion;
- fall back to the original prompt when extraction is empty.

- [ ] **Step 10: Implement semantic/hybrid reducer**

Create `SemanticToolDeclarationReducer.cs`:

```csharp
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Abstractions;

namespace OpenClaw.Plugins.ToolDeclarationReduction.Semantic;

public sealed class SemanticToolDeclarationReducer(ILogger logger) : IToolDeclarationReducer
{
    private readonly object _gate = new();
    private SemanticToolIndex? _index;

    public ValueTask<ToolDeclarationReductionResult> ReduceAsync(
        ToolDeclarationReductionContext context,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var config = context.Config;
        var hardMax = Math.Max(1, config.HardMaxTools);
        var maxTools = Math.Clamp(config.MaxTools, 1, hardMax);
        var minScore = Math.Clamp(config.MinScore, 0.0, 1.0);
        var index = GetOrBuildIndex(context.CandidateTools);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        MergeScores(scores, index.Search(context.UserMessage ?? string.Empty, hardMax, minScore));
        if (config.EnablePromptDistillation)
        {
            foreach (var phrase in PromptIntentDistiller.DistillActionPhrases(context.UserMessage ?? string.Empty))
                MergeScores(scores, index.Search(phrase, hardMax, minScore), discount: 0.92);
        }

        var selected = new List<Microsoft.Extensions.AI.AITool>();
        var pinned = new List<string>();
        var skippedPinned = new List<string>();
        var candidates = context.CandidateTools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);

        foreach (var requested in config.AlwaysIncludeTools.Where(static item => !string.IsNullOrWhiteSpace(item)))
        {
            if (selected.Count >= hardMax)
            {
                skippedPinned.Add(requested.Trim());
                continue;
            }

            if (candidates.TryGetValue(requested.Trim(), out var tool))
            {
                selected.Add(tool);
                pinned.Add(tool.Name);
                scores.TryAdd(tool.Name, 1.0);
            }
            else
            {
                skippedPinned.Add(requested.Trim());
            }
        }

        var ranked = context.CandidateTools
            .Where(tool => selected.All(existing => !string.Equals(existing.Name, tool.Name, StringComparison.Ordinal)))
            .Select(tool => new
            {
                Tool = tool,
                Score = IsHybrid(config.Mode)
                    ? (0.45 * LexicalScore(tool, context.UserMessage) + 0.55 * scores.GetValueOrDefault(tool.Name))
                    : scores.GetValueOrDefault(tool.Name)
            })
            .Where(item => item.Score >= minScore)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Tool.Name, StringComparer.Ordinal)
            .Take(Math.Max(0, maxTools - selected.Count));

        foreach (var item in ranked)
        {
            selected.Add(item.Tool);
            scores[item.Tool.Name] = item.Score;
        }

        var fallbackUsed = selected.Count == 0 && context.CandidateTools.Count > 0;
        logger.LogDebug("Semantic tool declaration reduction selected {SelectedCount} of {CandidateCount} tools.", selected.Count, context.CandidateTools.Count);

        return ValueTask.FromResult(new ToolDeclarationReductionResult
        {
            Tools = selected,
            Diagnostics = new ToolDeclarationReductionDiagnostics
            {
                Enabled = true,
                Mode = IsHybrid(config.Mode) ? "hybrid" : "semantic",
                CandidateCount = context.CandidateTools.Count,
                SelectedCount = selected.Count,
                MaxTools = maxTools,
                HardMaxTools = hardMax,
                PresetId = context.Preset?.PresetId,
                FallbackUsed = fallbackUsed,
                FallbackReason = fallbackUsed ? "semantic_no_results" : null,
                SelectedTools = selected.Select(static tool => tool.Name).ToArray(),
                PinnedTools = pinned,
                SkippedPinnedTools = skippedPinned,
                Scores = scores
            }
        });
    }

    private SemanticToolIndex GetOrBuildIndex(IReadOnlyList<Microsoft.Extensions.AI.AITool> tools)
    {
        var rebuilt = SemanticToolIndex.Build(tools);
        lock (_gate)
        {
            if (_index is null || !string.Equals(_index.Fingerprint, rebuilt.Fingerprint, StringComparison.Ordinal))
                _index = rebuilt;
            return _index;
        }
    }

    private static void MergeScores(Dictionary<string, double> scores, IReadOnlyList<SemanticToolSearchResult> results, double discount = 1.0)
    {
        foreach (var result in results)
        {
            var score = result.Score * discount;
            if (!scores.TryGetValue(result.Tool.Name, out var existing) || score > existing)
                scores[result.Tool.Name] = score;
        }
    }

    private static double LexicalScore(Microsoft.Extensions.AI.AITool tool, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return 0.0;
        return prompt.Contains(tool.Name, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    private static bool IsHybrid(string? mode)
        => string.Equals(mode, "hybrid", StringComparison.OrdinalIgnoreCase);
}
```

Behavior:

- respect `RouteToolsDisabled` indirectly by only using `CandidateTools` from the executor;
- apply `AlwaysIncludeTools` and `HardMaxTools` with the same semantics as the rule reducer;
- build or reuse `SemanticToolIndex` for the current candidate fingerprint;
- search the original prompt as the baseline query;
- if `EnablePromptDistillation=true`, search each deterministic action phrase and merge scores;
- in `Mode=semantic`, rank by semantic score;
- in `Mode=hybrid`, combine local lexical score and semantic score using `0.45 * lexical + 0.55 * semantic`;
- if semantic scoring returns no tools and `FallbackToRuleWhenSemanticUnavailable=true`, return empty diagnostics with `FallbackUsed=true` so the executor or selector can fall back to rule/preset behavior;
- produce diagnostics with `Mode="semantic"` or `Mode="hybrid"`, selected tools, pinned tools, skipped pinned tools, scores, and fallback reason.

The reducer must not call or reference ElBruno code. It should be small enough to audit, with deterministic behavior suitable for unit tests.

- [ ] **Step 11: Write semantic reducer tests**

Create `src/OpenClaw.Tests/SemanticToolDeclarationReducerTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenClaw.Core.Abstractions;
using OpenClaw.Core.Models;
using OpenClaw.Plugins.ToolDeclarationReduction.Semantic;
using System.Text.Json;
using Xunit;

namespace OpenClaw.Tests;

public sealed class SemanticToolDeclarationReducerTests
{
    [Fact]
    public async Task ReduceAsync_SemanticMode_RanksRelevantToolByDescriptionAndParameters()
    {
        var reducer = new SemanticToolDeclarationReducer(NullLogger.Instance);
        var tools = new[]
        {
            Tool("database", "Run database queries", "query connectionString"),
            Tool("browser", "Open a web page", "url"),
            Tool("message", "Send updates to a chat channel", "chatId text")
        };

        var result = await reducer.ReduceAsync(Context(tools, "send this update to the chat", "semantic"), TestContext.Current.CancellationToken);

        Assert.Equal("message", result.Tools[0].Name);
        Assert.Equal("semantic", result.Diagnostics.Mode);
        Assert.Contains("message", result.Diagnostics.Scores.Keys);
    }

    [Fact]
    public async Task ReduceAsync_HybridMode_CombinesExplicitToolNameAndSemanticScore()
    {
        var reducer = new SemanticToolDeclarationReducer(NullLogger.Instance);
        var tools = new[]
        {
            Tool("read_file", "Read files from disk", "path"),
            Tool("apply_patch", "Apply a source code patch", "patch"),
            Tool("message", "Send updates to a chat channel", "chatId text")
        };

        var result = await reducer.ReduceAsync(Context(tools, "use read_file then patch the code", "hybrid"), TestContext.Current.CancellationToken);

        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Contains(result.Tools, tool => tool.Name == "apply_patch");
        Assert.Equal("hybrid", result.Diagnostics.Mode);
    }

    [Fact]
    public void PromptIntentDistiller_ExtractsMultipleActionPhrases()
    {
        var phrases = PromptIntentDistiller.DistillActionPhrases("read the config, edit the port, then run tests");

        Assert.Contains(phrases, phrase => phrase.Contains("read", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(phrases, phrase => phrase.Contains("edit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(phrases, phrase => phrase.Contains("run", StringComparison.OrdinalIgnoreCase));
    }

    private static ToolDeclarationReductionContext Context(IReadOnlyList<AITool> tools, string prompt, string mode)
        => new()
        {
            Session = new Session { Id = "sess-semantic", ChannelId = "websocket", SenderId = "user1" },
            UserMessage = prompt,
            CandidateTools = tools,
            Config = new ToolDeclarationReductionConfig
            {
                Enabled = true,
                Mode = mode,
                MaxTools = 2,
                MinTools = 1,
                HardMaxTools = 3,
                MinScore = 0.0,
                EnablePromptDistillation = true
            }
        };

    private static AITool Tool(string name, string description, string parameterNames)
    {
        var properties = string.Join(",", parameterNames.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(static name => $"\"{name}\":{{\"type\":\"string\"}}"));
        using var schema = JsonDocument.Parse($$"{"type":"object","properties":{ {{{properties}}} }}");
        return AIFunctionFactory.CreateDeclaration(
            name,
            description,
            schema.RootElement.Clone(),
            returnJsonSchema: null);
    }
}
```

Use the same `AIFunctionFactory.CreateDeclaration` helper style from the rule reducer tests. Keep the tests deterministic; do not call network services, local ONNX models, or external LLMs.

- [ ] **Step 12: Add dependency-boundary tests for the semantic plugin**

Add a test that scans the semantic plugin project and source files:

```csharp
[Fact]
public void SemanticPlugin_DoesNotReferenceElBrunoPackagesOrRouter()
{
    var root = FindRepositoryRoot();
    var files = Directory.EnumerateFiles(Path.Combine(root, "src", "OpenClaw.Plugins.ToolDeclarationReduction.Semantic"), "*.*", SearchOption.AllDirectories)
        .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));

    foreach (var file in files)
    {
        var text = File.ReadAllText(file);
        Assert.DoesNotContain("ElBruno", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ModelContextProtocol.MCPToolRouter", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LocalEmbeddings", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LocalLLMs", text, StringComparison.OrdinalIgnoreCase);
    }
}
```

This test enforces the user's requirement: borrow the design, do not reference ElBruno's implementation.

- [ ] **Step 13: Add the plugin project to the solution**

Run the repo's preferred solution-add command for `OpenClaw.Net.slnx`. If `dotnet sln` supports the `.slnx` file in this SDK, run:

```powershell
dotnet sln OpenClaw.Net.slnx add src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/OpenClaw.Plugins.ToolDeclarationReduction.Semantic.csproj
```

If `.slnx` editing is not supported by the installed SDK, edit the solution using the existing repository pattern and validate with `dotnet build OpenClaw.Net.slnx --no-restore`.

- [ ] **Step 14: Run semantic plugin tests**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~SemanticToolDeclarationReducerTests|FullyQualifiedName~NativeDynamicToolDeclarationReducerPluginTests"
```

Expected: tests pass.

- [ ] **Step 15: Commit**

```powershell
git add src/OpenClaw.PluginKit/INativeDynamicPlugin.cs src/OpenClaw.Agent/Plugins/NativeDynamicPluginHost.cs src/OpenClaw.Agent/IAgentRuntimeFactory.cs src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntimeFactory.cs src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.cs src/OpenClaw.Gateway/Composition/RuntimeInitializationExtensions.RuntimeFactories.cs src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic src/OpenClaw.Tests/SemanticToolDeclarationReducerTests.cs src/OpenClaw.Tests/NativeDynamicToolDeclarationReducerPluginTests.cs OpenClaw.Net.slnx
git commit -m "feat(plugins): add semantic tool declaration reducer"
```

---

### Task 7: Documentation and Focused Validation

**Files:**
- Create: `docs/tool-declaration-reduction.md`
- Modify: `docs/README.md`

**Interfaces:**
- Consumes: completed core implementation and config names
- Produces: operator-facing documentation for rule mode, semantic/hybrid mode, defaults, diagnostics, plugin registration, and dependency boundaries

- [ ] **Step 1: Create operator documentation**

Create `docs/tool-declaration-reduction.md`:

````markdown
# Tool Declaration Reduction

Tool declaration reduction limits which function/tool schemas are sent to the model before each model call. It is different from TokenJuice: TokenJuice reduces tool results after execution, while declaration reduction reduces tool schemas before model invocation.

The default configuration is backward compatible. Reduction is available but disabled unless `Tooling:DeclarationReduction:Enabled` is set to `true`.

## Recommended rule-mode configuration

```json
{
  "Tooling": {
    "DeclarationReduction": {
      "Enabled": true,
      "Mode": "rule",
      "MaxTools": 16,
      "HardMaxTools": 24
    }
  }
}
```

## Defaults

| Setting | Default |
|---|---|
| `Enabled` | `false` |
| `Mode` | `rule` |
| `MaxTools` | `16` |
| `MinTools` | `4` |
| `HardMaxTools` | `24` |
| `MinScore` | `0.10` |
| `FallbackToPresetOnEmpty` | `true` |
| `FallbackToRuleWhenSemanticUnavailable` | `true` |
| `EnablePromptDistillation` | `false` |

## Permission boundaries

Reduction never widens tool access. `RouteToolsDisabled`, `Session.RouteAllowedTools`, resolved presets, approval, sandboxing, and governance remain authoritative.

## Runtime coverage

Native `AgentRuntime` and `MafAgentRuntime` both use the shared `OpenClawToolExecutor` declaration selection path, so the same session, prompt, and preset produce the same reduced declaration set.

## NativeAOT and semantic mode

Rule mode is AOT-safe and does not require embedding or local LLM dependencies. Semantic and hybrid modes are provided by `OpenClaw.Plugins.ToolDeclarationReduction.Semantic`, a JIT-only OpenClaw plugin. It borrows the ElBruno MCPToolRouter architecture of tool indexing, prompt intent distillation, and hybrid search, but it does not reference `ElBruno.*` packages or `ModelContextProtocol.MCPToolRouter`.

To enable semantic mode, load the semantic reducer plugin and set:

```json
{
    "Tooling": {
        "DeclarationReduction": {
            "Enabled": true,
            "Mode": "semantic",
            "MaxTools": 16,
            "HardMaxTools": 24,
            "EnablePromptDistillation": true
        }
    }
}
```

Use `Mode="hybrid"` to combine deterministic lexical scoring with semantic vector scoring.
````

- [ ] **Step 2: Register doc in docs README**

Modify `docs/README.md` by adding a row near `tokenjuice.md`:

```markdown
| [tool-declaration-reduction.md](tool-declaration-reduction.md) | Pre-LLM tool schema reduction for lowering function/tool declaration token cost. |
```

- [ ] **Step 3: Run focused tests for the feature**

Run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ToolDeclarationReduction|FullyQualifiedName~RuleBasedToolDeclarationReducer|FullyQualifiedName~SemanticToolDeclarationReducer|FullyQualifiedName~NativeDynamicToolDeclarationReducerPlugin|FullyQualifiedName~GetToolDeclarations|FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall"
```

Expected: tests pass.

- [ ] **Step 4: Run compile check for core runtime projects**

Run:

```powershell
dotnet build OpenClaw.Net.slnx --no-restore
```

Expected: build succeeds. If restore is needed because package assets are missing, run `dotnet build OpenClaw.Net.slnx` and record that restore occurred.

- [ ] **Step 5: Commit**

```powershell
git add docs/tool-declaration-reduction.md docs/README.md
git commit -m "docs: document tool declaration reduction"
```

---

## Final Verification

- [ ] Run all feature-focused tests:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~ToolDeclarationReduction|FullyQualifiedName~RuleBasedToolDeclarationReducer|FullyQualifiedName~SemanticToolDeclarationReducer|FullyQualifiedName~NativeDynamicToolDeclarationReducerPlugin|FullyQualifiedName~GetToolDeclarations|FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall"
```

Expected: tests pass.

- [ ] Run solution build:

```powershell
dotnet build OpenClaw.Net.slnx --no-restore
```

Expected: build succeeds, or succeeds after a normal restore if assets are missing.

- [ ] Inspect dependency boundary:

```powershell
Select-String -Path src/OpenClaw.Core/**/*.cs,src/OpenClaw.Agent/**/*.cs -Pattern "ElBruno|LocalEmbeddings|LocalLLMs|Onnx|ModelContextProtocol.MCPToolRouter"
```

Expected: no matches related to declaration reduction in `OpenClaw.Core` or `OpenClaw.Agent`.

- [ ] Inspect semantic plugin dependency boundary:

```powershell
Select-String -Path src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/**/*.cs,src/OpenClaw.Plugins.ToolDeclarationReduction.Semantic/*.csproj -Pattern "ElBruno|ModelContextProtocol.MCPToolRouter|LocalEmbeddings|LocalLLMs"
```

Expected: no output. The semantic reducer plugin must be OpenClaw-owned implementation code that borrows the architecture, not a package/source dependency on ElBruno.

- [ ] Inspect worktree:

```powershell
git status --short
```

Expected: clean worktree after the final commit.