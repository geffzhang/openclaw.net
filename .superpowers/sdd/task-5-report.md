# Task 5 Report: Runtime Wiring for Native and MAF

## What you implemented
- Wired `IToolDeclarationReducer` into gateway DI by registering `RuleBasedToolDeclarationReducer` in `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`.
- Extended `AgentRuntime` to accept an optional `IToolDeclarationReducer` and pass it into `OpenClawToolExecutor`.
- Updated `NativeAgentRuntimeFactory` to resolve and pass `IToolDeclarationReducer` from `AgentRuntimeFactoryContext.Services`.
- Updated native runtime model-call tool declaration selection to use `_toolExecutor.GetToolDeclarations(session, userMessage)` for both non-streaming and streaming calls.
- Updated native turn-routing probe setup to use `_toolExecutor.GetToolDeclarations(session, userMessage, new ToolDeclarationReductionRequest { IsTurnRoutingProbe = true })`.
- Updated `MafAgentRuntime` to resolve and pass `IToolDeclarationReducer` into `OpenClawToolExecutor`.
- Updated MAF agent creation to select tools through `_toolExecutor.GetToolDeclarations(session, userMessage)` before mapping to MAF tools.
- Updated MAF turn-routing probe setup to use the reduction-aware overload with `IsTurnRoutingProbe = true`.
- Added a focused MAF parity test proving declaration reduction now occurs before the model call.

## What you tested and test results
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall`
  - Result after implementation: PASS (`1 passed, 0 failed`)
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_FiltersToolsByPresetResolver"`
  - Result: PASS (`2 passed, 0 failed`)
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_TurnRoutingPolicy_FiltersTools_And_AppendsScopedPrompt|FullyQualifiedName~GetToolDeclarations_WhenReductionEnabled_UsesReducerAndForwardsUserMessage"`
  - Result: PASS (`2 passed, 0 failed`)

## TDD Evidence
### RED
- Command:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall`
- Output summary:
  - FAIL
  - Expected: `["echo_tool"]`
  - Actual: `["echo_tool", "shell"]`
  - Failure location: `src/OpenClaw.Tests/MafAdapterTests.cs:559`

### GREEN
- Command:
  - `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall`
- Output summary:
  - PASS
  - Test summary: `Total: 1, Failed: 0, Passed: 1, Skipped: 0`

## Files changed
- `src/OpenClaw.Agent/AgentRuntime.cs`
- `src/OpenClaw.Agent/NativeAgentRuntimeFactory.cs`
- `src/OpenClaw.Gateway/Composition/CoreServicesExtensions.cs`
- `src/OpenClaw.MicrosoftAgentFrameworkAdapter/MafAgentRuntime.cs`
- `src/OpenClaw.Tests/MafAdapterTests.cs`

## Self-review findings
- The change is narrowly scoped to runtime wiring and tool declaration selection.
- Reduction remains fail-open because `OpenClawToolExecutor` still returns preset-allowed candidates whenever declaration reduction is disabled, off, missing, empty-with-fallback, or throws.
- Native and MAF turn-routing probes now share the same reduction-aware declaration path by setting `IsTurnRoutingProbe = true`.
- No additional compatibility claims were added beyond the focused tests listed above.

## Issues or concerns
- No blocking issues found.
- I did not add a new native end-to-end reduction-specific test in `AgentRuntimeTests`; native coverage here relies on existing runtime routing coverage plus focused `OpenClawToolExecutor` reducer-forwarding coverage.

## Task 5 Native Test Fix
- Added native runtime parity coverage in `src/OpenClaw.Tests/AgentRuntimeTests.cs` for declaration reduction before the model call and for the reduction-aware turn-routing probe path.
- Added a lightweight recording reducer helper in `src/OpenClaw.Tests/AgentRuntimeTests.cs` to capture reducer contexts without changing production code.

### Commands and output summary
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~RunAsync_DeclarationReductionEnabled_ReducesToolsBeforeModelCall_And_ForwardsUserMessage|FullyQualifiedName~RunAsync_TurnRoutingProbe_UsesReductionAwareDeclarations_ForRoutingPolicy"`
  - First run: FAIL at build time due xUnit analyzer `xUnit2031` on `Assert.Single(...Where(...))` in the new tests.
  - Second run after fixing assertions: PASS (`Total: 2, Failed: 0, Passed: 2, Skipped: 0`).
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_FiltersToolsByPresetResolver"`
  - PASS (`Total: 2, Failed: 0, Passed: 2, Skipped: 0`).

## Task 5 MAF Test Fix
- Added `MafAgentRuntime_ForwardsUserMessageToDeclarationReducer` in `src/OpenClaw.Tests/MafAdapterTests.cs` to prove the runtime forwards the actual `userMessage` into `IToolDeclarationReducer` before the model call and still exposes only the reduced tool list to `CapturingLlmExecutionService`.
- Added `MafAgentRuntime_TurnRoutingProbe_UsesReducedTools` in `src/OpenClaw.Tests/MafAdapterTests.cs` to prove `ApplyTurnRoutingAsync` uses the reduction-aware probe path, with assertions on reduced `BaseOptions.Tools` and `IsTurnRoutingProbe == true` in the reducer context.
- Expanded the local test reducer helper in `src/OpenClaw.Tests/MafAdapterTests.cs` to record every `ToolDeclarationReductionContext` so probe and model-call invocations can be asserted independently.

### Commands and output summary
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_ForwardsUserMessageToDeclarationReducer|FullyQualifiedName~MafAgentRuntime_TurnRoutingProbe_UsesReducedTools"`
  - First run: FAIL at build time.
  - Output summary: `ToolDeclarationReductionContext` does not expose `Request`; nullable warning on `capturedRequest.BaseOptions.Tools` needed a concrete assertion.
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_ForwardsUserMessageToDeclarationReducer|FullyQualifiedName~MafAgentRuntime_TurnRoutingProbe_UsesReducedTools"`
  - Second run after fixing the assertions: PASS (`Total: 3, Failed: 0, Passed: 3, Skipped: 0`).
- `dotnet test src/OpenClaw.Tests/OpenClaw.Tests.csproj --filter "FullyQualifiedName~MafAgentRuntime_ReducesToolDeclarationsBeforeModelCall|FullyQualifiedName~MafAgentRuntime_ForwardsUserMessageToDeclarationReducer|FullyQualifiedName~MafAgentRuntime_TurnRoutingProbe_UsesReducedTools|FullyQualifiedName~MafAgentRuntime_FiltersToolsByPresetResolver"`
  - PASS (`Total: 4, Failed: 0, Passed: 4, Skipped: 0`).
