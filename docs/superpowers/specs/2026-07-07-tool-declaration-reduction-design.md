# Tool Declaration Reduction Design

Date: 2026-07-07
Status: Draft - awaiting user review
Reference: `E:\GitHub\ElBruno.ModelContextProtocol\src\ElBruno.ModelContextProtocol.MCPToolRouter`

## Summary

OpenClaw.NET currently sends the full preset-allowed tool declaration set to the LLM before each model call. With roughly 80 registered tools, full function/tool schema transmission creates avoidable prompt-token cost and makes tool selection noisier. Existing TokenJuice support reduces tool results after execution, but it does not reduce tool declarations before model invocation.

Add a tool declaration reduction layer inside `OpenClawToolExecutor`, where both the native `AgentRuntime` and `MafAgentRuntime` already obtain tool declarations. The layer will first provide an AOT-safe rule-based reducer in the core runtime path, then allow an optional JIT-only semantic reducer inspired by `ElBruno.ModelContextProtocol.MCPToolRouter` for embedding search and prompt distillation.

The recommended default is conservative: the feature is configured but disabled by default for backward compatibility. Operators can enable `rule` mode safely in NativeAOT deployments. JIT deployments can opt into `semantic` or `hybrid` mode when the optional semantic package is present.

## Goals

### P0: Reduce pre-LLM tool schema tokens

Only send the most relevant preset-allowed tool declarations for the current model call. The first target is reducing an 80-tool declaration set to a default maximum of 16 tools, with a hard cap of 24 when pinned or companion tools need to be preserved.

### P0: Improve tool hit rate without widening permissions

The reducer ranks tools by current user intent, session state, recent tool usage, and optional semantic similarity. It must never add tools that were excluded by `RouteToolsDisabled`, `Session.RouteAllowedTools`, a resolved preset, or governance policy.

### P0: Preserve native and MAF runtime parity

Both `AgentRuntime` and `MafAgentRuntime` must use the same declaration reduction path. MAF must not regress to full tool declarations while native runtime uses the reducer.

### P0: Preserve NativeAOT friendliness

The core reducer must avoid reflection-heavy and trim-unsafe dependencies. Semantic embedding, local LLM distillation, ONNX model loading, and ElBruno-derived integrations belong in an optional JIT-only implementation.

### P1: Make routing observable and diagnosable

Each reduction pass should produce diagnostics showing mode, candidate count, selected count, fallback behavior, pinned tools, and score summaries. Diagnostics should support logging, tests, and future admin/debug endpoints.

## Non-Goals

- Do not replace `ToolExecutionRouter`; execution routing remains about where a selected tool runs.
- Do not replace `ToolPresetResolver`; presets remain the first hard allowlist boundary.
- Do not bypass approval, sandbox, governance, plan-execute-verify, or audit controls.
- Do not summarize, rewrite, or trim individual tool JSON schemas in the first version.
- Do not make semantic embedding dependencies mandatory for core runtime or NativeAOT builds.
- Do not claim better compatibility without tests for native and MAF paths.

## Key Design Decisions

| Decision | Choice | Reason |
|---|---|---|
| Integration point | `OpenClawToolExecutor` declaration selection | Native and MAF already share this tool declaration boundary |
| Runtime coverage | Both `AgentRuntime` and `MafAgentRuntime` call the reduced declaration path | Prevents orchestrator-specific behavior |
| Default max tools | `MaxTools = 16`, `MinTools = 4`, `HardMaxTools = 24` | Strong token reduction while leaving room for multi-tool tasks |
| Default enablement | Config present, disabled by default | Avoids changing existing model behavior unexpectedly |
| Baseline reducer | AOT-safe rule scorer | Gives immediate value without JIT-only dependencies |
| Semantic reducer | Optional JIT-only implementation | Keeps ElBruno-style embedding search available without burdening core |
| Fallback posture | Fail open to preset-allowed candidates | Reduction failure must not hide available tools |

## Architecture

### Components

| Component | Existing/New | Responsibility |
|---|---|---|
| `OpenClawToolExecutor` | Existing, extended | Owns the single public tool declaration selection path |
| `IToolDeclarationReducer` | New | Ranks and filters preset-allowed tool declarations for one model call |
| `ToolDeclarationReductionContext` | New | Carries session, user message, candidates, preset, recent tool signals, and cancellation |
| `ToolDeclarationReductionResult` | New | Returns selected declarations and diagnostics |
| `ToolDeclarationReductionDiagnostics` | New | Records mode, counts, scores, fallback reason, and selected tools |
| `RuleBasedToolDeclarationReducer` | New | AOT-safe deterministic reducer in the core path |
| `SemanticToolDeclarationReducer` | New optional package | JIT-only embedding/distillation reducer based on the ElBruno reference design |
| `ToolDeclarationReductionConfig` | New | Configuration under `GatewayConfig.Tooling` |

### Layering

Core runtime projects should only know about `IToolDeclarationReducer` and the rule-based implementation. The semantic implementation should live in a plugin or optional integration project so NativeAOT builds do not transitively reference local embedding, local LLM, ONNX, or MCP router packages.

Recommended project placement:

```plaintext
OpenClaw.Core/
  Abstractions/
    IToolDeclarationReducer.cs
    ToolDeclarationReductionContext.cs
    ToolDeclarationReductionResult.cs

OpenClaw.Agent/
  ToolDeclarations/
    RuleBasedToolDeclarationReducer.cs
    ToolDeclarationScorer.cs

OpenClaw.Plugins.ToolDeclarationReduction.Semantic/   optional JIT-only
  SemanticToolDeclarationReducer.cs
  SemanticToolIndex.cs
  PromptIntentDistiller.cs
```

The exact semantic package name can change during implementation, but the dependency direction must not: core and agent runtime must not require the optional semantic package.

## Configuration

Add declaration reduction configuration under `GatewayConfig.Tooling`:

```csharp
public ToolDeclarationReductionConfig DeclarationReduction { get; set; } = new();

public sealed class ToolDeclarationReductionConfig
{
    public bool Enabled { get; set; } = false;
    public string Mode { get; set; } = "rule"; // off, rule, semantic, hybrid
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

Default behavior is equivalent to current behavior because `Enabled=false`. Recommended operator configuration for early adopters:

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

`AlwaysIncludeTools` should count toward `MaxTools` but may expand selection up to `HardMaxTools`. Tools in `AlwaysIncludeTools` are still subject to preset and route allowlists. `NeverAutoIncludeTools` prevents low-confidence automatic inclusion, but an explicit user mention can still rank the tool if the tool is otherwise allowed.

## Runtime Flow

### Native runtime

Native runtime should call the reduced declaration path before each model invocation, including subsequent loop iterations:

```plaintext
RunTurnAsync
  -> ApplyTurnRoutingAsync
       -> BaseOptions.Tools = GetToolDeclarations(session, userMessage, reductionContext)
  -> BuildMessages
  -> ChatOptions.Tools = GetToolDeclarations(session, userMessage, reductionContext)
  -> LLM
  -> tool calls
  -> ExecuteAsync
  -> next iteration repeats declaration reduction
```

Reduction should happen per model call rather than once per turn. Tool needs can shift after a tool result enters history.

### MAF runtime

`MafAgentRuntime` must use the same `OpenClawToolExecutor` reducer path in both places where it currently requests tool declarations:

```plaintext
ApplyTurnRoutingAsync
  -> baseOptions.Tools = GetToolDeclarations(session, userMessage, reductionContext)

CreateAgent(session, userMessage)
  -> reducedDeclarations = GetToolDeclarations(session, userMessage, reductionContext)
  -> map declaration names to _mafToolsByName
  -> ChatClientAgent
```

This preserves MAF adapter behavior while ensuring it does not send the full declaration set when native runtime would reduce it.

### Tool executor API

Keep the existing API as a compatibility path:

```csharp
public IList<AITool> GetToolDeclarations(Session session)
```

Add a richer overload for runtime calls:

```csharp
public IList<AITool> GetToolDeclarations(
    Session session,
    string? userMessage,
    ToolDeclarationReductionRequest? request = null)
```

The existing overload should keep current behavior when reduction is disabled. Runtime call sites that know the current user message should use the richer overload.

`ToolDeclarationReductionRequest` is a small per-call hint object. It keeps runtime-specific signals out of `Session` while letting native and MAF pass equivalent context:

```csharp
public sealed class ToolDeclarationReductionRequest
{
  public IReadOnlyList<string> RecentToolNames { get; init; } = [];
  public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
    = new Dictionary<string, int>(StringComparer.Ordinal);
  public bool IsTurnRoutingProbe { get; init; }
}
```

The first implementation can pass an empty request and derive recent tool names from session history. The type exists so later runtime-specific signals do not require another public API shape change.

## Reducer Contract

Recommended contract:

```csharp
public interface IToolDeclarationReducer
{
    ValueTask<ToolDeclarationReductionResult> ReduceAsync(
        ToolDeclarationReductionContext context,
        CancellationToken ct);
}
```

Context:

```csharp
public sealed class ToolDeclarationReductionContext
{
    public required Session Session { get; init; }
    public string? UserMessage { get; init; }
    public required IReadOnlyList<AITool> CandidateTools { get; init; }
    public ResolvedToolPreset? Preset { get; init; }
    public IReadOnlyList<string> RecentToolNames { get; init; } = [];
    public IReadOnlyDictionary<string, int> RecentToolFailures { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
}
```

Result:

```csharp
public sealed class ToolDeclarationReductionResult
{
    public required IReadOnlyList<AITool> Tools { get; init; }
    public required ToolDeclarationReductionDiagnostics Diagnostics { get; init; }
}
```

The reducer may return the same candidate objects it received. It should not mutate declarations.

## Rule-Based Scoring

The AOT-safe scorer should be deterministic, explainable, and cheap. Recommended features:

```plaintext
score =
  + exact tool name mention
  + tool name token match
  + description token match
  + parameter name token match
  + preset or surface relevance boost
  + recent successful use boost
  - recent failure or blocked execution penalty
  - high-risk uncertainty penalty
```

Rules:

- Exact tool name mentions outrank fuzzy matches when the tool is allowed.
- Parameter names participate in matching because generic tools often expose intent through schema fields such as `path`, `query`, `url`, `chatId`, or `command`.
- Recent successful tools receive a bounded boost for follow-up prompts such as "continue" or "do the same for this file".
- Recently failed, blocked, or denied tools receive a soft penalty, not a hard ban.
- High-risk tools such as `shell`, `process`, `write_file`, and `code_exec` receive a soft penalty for ambiguous prompts. Approval and governance still decide execution.
- Companion tools can be added for common workflows, such as keeping `read_file` near `edit_file` and `apply_patch` in coding presets.

Tokenization should be culture-invariant, case-insensitive, and allocation-conscious. The initial implementation can use simple ASCII-oriented splitting over names, descriptions, and JSON schema text. It should avoid reflection-based schema inspection.

## Semantic Reducer Mapping

The optional semantic implementation should follow the ElBruno reference architecture without making it a core dependency:

| ElBruno concept | OpenClaw mapping |
|---|---|
| `ToolIndex` | Semantic index over preset-eligible tool name, description, and parameters |
| `PromptDistiller` | Optional user prompt to action-phrase distillation |
| Hybrid search | Baseline original prompt search plus phrase-level searches |
| `TopK` | `MaxTools` with `HardMaxTools` for pinned and companion tools |
| `MinScore` | `DeclarationReduction.MinScore` |

In `hybrid` mode, combine rule and semantic scores:

```plaintext
finalScore = 0.45 * ruleScore + 0.55 * semanticScore
```

The exact weights should be configurable or constants in the first implementation. Semantic ranking should not override allowlists. Prompt distillation timeouts fall back to the original user message.

## Fallback and Error Handling

Reduction must fail open:

- `Mode=off` returns the current preset-allowed tool set.
- `RouteToolsDisabled=true` returns an empty set and bypasses reducers.
- Reducer exceptions log a warning and return preset-allowed candidates.
- Empty reducer results fall back to preset-allowed candidates when `FallbackToPresetOnEmpty=true`.
- Missing semantic dependencies in `hybrid` mode fall back to rule mode when `FallbackToRuleWhenSemanticUnavailable=true`.
- Prompt distillation timeout or invalid output uses the original prompt.
- Pinned tools that do not exist or are not allowed are recorded in diagnostics and skipped.
- Tool collection changes invalidate semantic indexes by version/hash and trigger rebuild.

The reducer must not hide execution-time errors. If the LLM calls a tool that was not declared for the current model call, existing unknown-tool handling remains valid.

## Diagnostics

Each reduction pass should produce structured diagnostics:

```plaintext
mode=rule enabled=true preset=coding candidates=53 selected=16 max=16 hardMax=24
fallback=false fallbackReason=
selected=read_file,edit_file,apply_patch,shell,git,...
pinned=read_file skippedPinned=none
scoreSummary=read_file:1.00,edit_file:0.82,apply_patch:0.79
```

Initial diagnostics can be logged at debug level, with warnings for fallbacks. Future admin endpoints can surface recent reduction decisions, but the first implementation does not require a UI.

## Testing Strategy

### Compatibility

- Default configuration returns the same declarations as current behavior.
- Existing `GetToolDeclarations(session)` callers keep compiling and behave unchanged unless reduction is enabled.
- `Mode=off` is equivalent to disabled mode.

### Preset and route boundaries

- `RouteToolsDisabled` returns no declarations.
- `Session.RouteAllowedTools` remains a hard allowlist.
- Resolved presets are applied before reduction.
- `AlwaysIncludeTools` cannot add a preset-denied tool.

### Rule reducer behavior

- "read a file" ranks `read_file` high.
- "edit config" ranks `edit_file` and `apply_patch` high.
- "open this URL" ranks `browser` or `web_fetch` high when allowed.
- Follow-up prompts preserve recent relevant tools.
- Ambiguous prompts do not over-select high-risk tools unless they are recent, explicit, or companion tools.

### Fallback

- Reducer exception returns preset-allowed declarations.
- Empty reduction falls back according to configuration.
- Missing pinned tools are skipped and recorded.
- Semantic unavailable in `hybrid` mode falls back to rule mode.

### Native and MAF parity

- `AgentRuntime` uses reduced declarations in normal model calls and turn-routing base options.
- `MafAgentRuntime.ApplyTurnRoutingAsync` uses reduced declarations.
- `MafAgentRuntime.CreateAgent` maps reduced declaration names to MAF adapters.
- The same session, prompt, and preset produce equivalent tool-name sets in native and MAF paths.

### AOT/JIT boundary

- Core rule reducer builds without semantic dependencies.
- Source generation is used for any new config serialization shape required by NativeAOT.
- Semantic reducer tests live in the optional JIT project.

## Documentation

Add user-facing documentation after implementation in `docs/tool-declaration-reduction.md` and register it in `docs/README.md` if the feature is exposed to operators. The doc should cover:

- What declaration reduction is and how it differs from TokenJuice.
- Default values and recommended enablement.
- NativeAOT versus JIT semantic mode implications.
- Native and MAF runtime parity.
- Diagnostics and fallback behavior.
- ElBruno MCPToolRouter as a reference implementation.

## Rollout Plan

1. Add config, reducer contracts, and diagnostics models.
2. Add rule-based reducer and unit tests.
3. Wire `OpenClawToolExecutor` with backward-compatible overloads.
4. Update `AgentRuntime` and `MafAgentRuntime` call sites to pass the current user message.
5. Add native and MAF parity tests.
6. Add operator documentation for rule mode.
7. Add optional semantic reducer package and tests as a separate follow-up.

## OpenClaw-Specific Constraints

- Core runtime must remain lightweight and NativeAOT friendly.
- Optional integrations must remain optional.
- Public-bind hardening and governance take precedence over convenience.
- Tool execution paths remain behind the existing tool execution layer.
- Compatibility claims require tests across native and MAF paths.