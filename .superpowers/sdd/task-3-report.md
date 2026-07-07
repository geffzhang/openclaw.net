# What you implemented

- Added `ToolDeclarationText` helper in `OpenClaw.Agent.ToolDeclarations` to build deterministic searchable text from `AITool` name, description, and JSON schema.
- Added `RuleBasedToolDeclarationReducer : IToolDeclarationReducer` with deterministic rule scoring, pinned tool handling, hard/max/min tool bounds, recent tool boosts, recent failure penalties, high-risk tool penalty, and diagnostics.
- Added focused reducer tests for explicit tool-name ranking, parameter-name scoring, and pinned tools respecting `HardMaxTools`.
- No runtime path was wired or changed; the reducer is available only as a new type for later tasks.

# What you tested and test results

- Command: `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests`
- Final result: passed, 3 total, 0 failed, 3 passed, 0 skipped.

# TDD Evidence if TDD was required: RED command/output and GREEN command/output

RED command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

RED output:

```text
OpenClaw.Tests net10.0 failed with CS0234: The type or namespace name 'ToolDeclarations' does not exist in the namespace 'OpenClaw.Agent'.
Command exited with code 1
```

GREEN command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

GREEN output:

```text
Test summary: total: 3, failed: 0, passed: 3, skipped: 0.
Build succeeded.
```

# Files changed

- `src/OpenClaw.Agent/ToolDeclarations/ToolDeclarationText.cs`
- `src/OpenClaw.Agent/ToolDeclarations/RuleBasedToolDeclarationReducer.cs`
- `src/OpenClaw.Tests/RuleBasedToolDeclarationReducerTests.cs`
- `.superpowers/sdd/task-3-report.md`

# Self-review findings

- Scope review: implementation is limited to the new helper, reducer, and focused tests; no executor, runtime, or DI wiring was changed.
- AOT review: implementation uses deterministic string/token processing and existing Microsoft.Extensions.AI abstractions; no reflection-heavy dependencies were added.
- Behavior review: existing runtime behavior is preserved because the new reducer is not yet called from any runtime path.
- Patch review: `git show --check --stat HEAD` reported no whitespace errors for the committed code.

# Issues or concerns

- The test helper in the brief used raw string syntax that did not compile in this repository, so it was adapted to normal interpolated JSON string construction while preserving the same schema content.
- No external reviewer subagent tool was available in this VS Code tool environment, so self-review was performed locally against the committed scope and verification output.

# Review fix: MinScore and MinTools selection phases

- Fixed `RuleBasedToolDeclarationReducer` so above-threshold candidates are selected first up to `MaxTools`, and below-threshold candidates are only backfilled as needed to reach `MinTools`, while preserving `HardMaxTools` and pinned-tool handling.
- Added focused regression coverage for `MinScore=0.5`, `MinTools=2`, and `MaxTools=4` proving zero-score tools do not fill to `MaxTools`.

RED command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

RED output summary:

```text
Failed: ReduceAsync_BackfillsBelowMinScoreOnlyToMinTools expected ["read_file", "irrelevant_alpha"] but actual selected ["read_file", "irrelevant_alpha", "irrelevant_beta", "irrelevant_gamma"].
Test summary: total 4, failed 1, passed 3, skipped 0.
```

GREEN command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

GREEN output summary:

```text
Test summary: total 4, failed 0, passed 4, skipped 0.
Build succeeded.
```

# Re-review fix: defensive route/preset filtering and NeverAutoInclude

- Fixed `RuleBasedToolDeclarationReducer` so `RouteToolsDisabled` returns no tools, route and preset allowlists constrain both automatic candidates and `AlwaysIncludeTools`, and `NeverAutoIncludeTools` blocks automatic scored/backfill selection without blocking otherwise-allowed `AlwaysIncludeTools`.
- Added focused regression coverage for route disabled, route/preset allowlist intersection, automatic NeverAuto exclusion, and AlwaysInclude plus NeverAuto behavior.

Command run:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

Output summary:

```text
Test summary: total 8, failed 0, passed 8, skipped 0.
Build succeeded.
```

# Re-review fix: exact tool identity and allowlist matching

- Fixed `RuleBasedToolDeclarationReducer` so tool identity matching, route/preset allowlist membership, NeverAutoInclude filtering, pinned de-duplication, diagnostics score insertion, and high-risk tool identity checks use exact ordinal comparisons. Prompt/token text scoring remains case-insensitive.
- Added focused regression coverage proving a route allowlist entry `read_file` does not allow a candidate named `READ_FILE`.

RED command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

RED output summary:

```text
Failed: ReduceAsync_RouteAllowlistUsesExactToolIdentity expected no selected tools, but actual selected [READ_FILE].
Test summary: total 9, failed 1, passed 8, skipped 0.
```

GREEN command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~RuleBasedToolDeclarationReducerTests
```

GREEN output summary:

```text
Test summary: total 9, failed 0, passed 9, skipped 0.
Build succeeded.
```