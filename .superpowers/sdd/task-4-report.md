# Task 4 Report: OpenClawToolExecutor Integration

## What you implemented

- Added an optional `IToolDeclarationReducer` dependency to `OpenClawToolExecutor`.
- Added `GetToolDeclarations(Session session, string? userMessage, ToolDeclarationReductionRequest? request = null)`.
- Preserved the existing `GetToolDeclarations(Session session)` API as a compatibility wrapper.
- Kept existing declaration behavior when route tools are disabled, declaration reduction is disabled, mode is `off`, or no reducer is configured.
- Applied declaration reduction only after existing route/preset filtering has produced the candidate declaration set.
- Passed user message, candidate tools, preset, config, recent tool names, recent failures, and turn-routing probe state into the reducer context.
- Implemented fail-open behavior for reducer exceptions and empty reducer results when `FallbackToPresetOnEmpty` is enabled.
- AOT/JIT implications: no reflection, dynamic loading, or new dependencies were introduced; the synchronous wait matches the existing synchronous declaration API and is limited to the planned CPU-only reducers.

## What you tested and test results

- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests.GetToolDeclarations"`
- Result after implementation: passed, 3 total, 0 failed, 3 passed, 0 skipped.
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests"`
- Result after implementation: passed, 19 total, 0 failed, 19 passed, 0 skipped.

## TDD Evidence if TDD was required: RED command/output and GREEN command/output

TDD was required and followed.

RED command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests.GetToolDeclarations"
```

RED output summary:

```text
OpenClaw.Tests net10.0 failed, 4 errors
OpenClawToolExecutorTests.cs(484,30): error CS1501: GetToolDeclarations method has no overload that takes 2 arguments
OpenClawToolExecutorTests.cs(500,30): error CS1501: GetToolDeclarations method has no overload that takes 2 arguments
OpenClawToolExecutorTests.cs(516,30): error CS1501: GetToolDeclarations method has no overload that takes 2 arguments
OpenClawToolExecutorTests.cs(538,13): error CS1739: OpenClawToolExecutor best overload does not have a parameter named toolDeclarationReducer
Build failed with 4 errors
```

GREEN command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests.GetToolDeclarations"
```

GREEN output summary:

```text
OpenClaw.Tests test net10.0 passed
Test summary: total: 3, failed: 0, passed: 3, skipped: 0, duration: 3.6 seconds
Build succeeded
```

Additional focused validation command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests"
```

Additional focused validation output summary:

```text
OpenClaw.Tests test net10.0 passed
Test summary: total: 19, failed: 0, passed: 19, skipped: 0, duration: 3.8 seconds
Build succeeded
```

## Files changed

- `src/OpenClaw.Agent/OpenClawToolExecutor.cs`
- `src/OpenClaw.Tests/OpenClawToolExecutorTests.cs`
- `.superpowers/sdd/task-4-report.md`

Committed code/test changes:

```text
f99c6a0 feat(agent): reduce tool declarations in executor
```

## Self-review findings

- The reducer is optional and cannot affect existing runtime behavior unless declaration reduction is enabled and a reducer is provided.
- The existing synchronous API is preserved; the old overload delegates to the new overload with a null user message and null request.
- Candidate declarations are computed before reduction using the existing session route/preset filter, so reducer input is constrained to the previously allowed declaration set.
- Reducer exceptions fail open to the preset-allowed candidate list and log a warning.
- Empty reducer output falls back to the preset-allowed candidate list when `FallbackToPresetOnEmpty` is true, matching the brief.
- No NativeAOT-hostile dependencies or reflection-heavy behavior were introduced.

## Issues or concerns

- The new overload blocks synchronously on `ReduceAsync` because the current declaration API is synchronous, as specified by the brief. If a future reducer performs external model or embedding calls, an async runtime declaration path should be added instead of blocking here.
- This task wires only `OpenClawToolExecutor`; native and MAF runtimes still pass no user message until later tasks wire those runtime call sites.

## Fix follow-up

- Fixed the reducer output trust boundary in `OpenClawToolExecutor` by post-filtering reducer output against the original allowed candidate set before returning.
- Preserved reducer order for allowed tools and kept fail-open fallback to the preset-allowed candidate set when filtered output is empty and `FallbackToPresetOnEmpty` is enabled.
- Added regression coverage for disallowed reducer output and empty reducer fallback in `OpenClawToolExecutorTests`.

Validation command:

```powershell
dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~OpenClawToolExecutorTests"
```

Output summary:

```text
OpenClaw.Tests passed
Test summary: total: 19, failed: 0, passed: 19, skipped: 0
```