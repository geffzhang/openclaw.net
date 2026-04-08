# OpenClaw.Evaluation — Agent Evaluation Framework

> **Status**: Experimental — use for non-production quality gates and CI feedback loops.

`OpenClaw.Evaluation` is a lightweight, NativeAOT-friendly library that bridges the OpenClaw runtime's telemetry system with a strongly-typed evaluation and assertion framework. It enables "Evaluation-as-Code" — automated quality gates for agent behavior expressed as standard xUnit tests.

## Core Concepts

| Concept | Description |
|---------|-------------|
| **Execution Trace** | A structured capture of all tool invocations and LLM calls within a single agent session, extracted from .NET `Activity` traces. |
| **Fluent Assertions** | A chainable API (`HaveCalledTool`, `BeforeTool`, `HaveNoErrors`) for verifying tool-call sequences and outcomes. |
| **Telemetry Bridge** | An `InMemoryTraceCollector` that subscribes to the OpenClaw `ActivitySource` and feeds data into the `ActivityTraceExtractor`. |
| **Evaluation Result** | A wrapper carrying quality scores (faithfulness, relevance, fluency) alongside the execution trace. |

## Quick Start

### 1. Add the project reference

```xml
<ProjectReference Include="..\OpenClaw.Evaluation\OpenClaw.Evaluation.csproj" />
```

### 2. Capture traces in a test

```csharp
using OpenClaw.Evaluation.Telemetry;
using OpenClaw.Evaluation.Assertions;

public sealed class MyAgentTests : IDisposable
{
    private readonly InMemoryTraceCollector _collector = new();

    public void Dispose() => _collector.Dispose();

    [Fact]
    public void Agent_Should_Search_Before_Booking()
    {
        // ... trigger agent execution that emits Activity traces ...

        var trace = ActivityTraceExtractor.ExtractTrace(
            _collector.GetActivities(), sessionId);

        trace.Should()
            .HaveCalledTool("SearchFlights")
            .BeforeTool("BookFlight")
            .And()
            .HaveNoErrors()
            .HaveCallCountAtMost(5)
            .Validate();
    }
}
```

### 3. Use quality scores for RAG evaluation

```csharp
var result = new EvaluationResult
{
    Trace = trace,
    FaithfulnessScore = 0.95,
    ContextRelevanceScore = 0.90,
    FluencyScore = 0.88
};

Assert.True(result.MeetsQualityGate(0.85));
```

## Assertion API Reference

| Method | Description |
|--------|-------------|
| `HaveCalledTool(name)` | Asserts the tool was invoked at least once. Returns a `ToolCallChain` for ordering. |
| `.BeforeTool(name)` | Asserts the previous tool was called before this one (temporal ordering). |
| `.And()` | Returns to the parent assertion context for further chaining. |
| `HaveNoErrors()` | Asserts all tool invocations succeeded. |
| `HaveErrors()` | Asserts at least one tool invocation failed (for red-team tests). |
| `HaveCallCountAtMost(n)` | Asserts total tool calls do not exceed the circuit-breaker threshold. |
| `NotHaveCalledTool(name)` | Asserts the tool was never called. |
| `Validate()` | Finalizes the chain and throws `EvaluationAssertionException` on any failures. |

## Telemetry Bridge

The `InMemoryTraceCollector` listens to `ActivitySource` events from `OpenClaw.Gateway` (configurable). Activities tagged with `session.id`, `tool.name`, `ok`, `input.tokens`, `output.tokens`, `error.message`, and `timed_out` are parsed by `ActivityTraceExtractor` into `AgentExecutionTrace` models.

```
OpenClaw Runtime ──▶ ActivitySource ──▶ InMemoryTraceCollector ──▶ ActivityTraceExtractor ──▶ AgentExecutionTrace
                                                                                                      │
                                                                                              Fluent Assertions
                                                                                              EvaluationResult
```

## Design Principles

- **NativeAOT-safe**: No reflection, no dynamic dispatch. All models are records/sealed classes.
- **Zero-coupling**: Does not modify any existing OpenClaw runtime code. Reads from the existing telemetry surface.
- **Strong typing**: Compile-time validation of assertion chains, tool names as strings only at the boundary.
- **Optional**: This library is an opt-in dependency for test projects. It does not ship with the gateway.

## Security Testing Pattern

For red-team / prompt-injection boundary tests:

```csharp
trace.Should()
    .HaveCalledTool("shell")
    .And()
    .HaveErrors()  // sandbox should have blocked execution
    .Validate();
```

## Limitations

- Quality scores (`FaithfulnessScore`, `ContextRelevanceScore`, `FluencyScore`) must be computed externally (e.g., via an LLM-as-Judge pattern) and set on `EvaluationResult` manually. The library provides the model and gate-check, not the scoring engine itself.
- `InMemoryTraceCollector` captures process-wide `Activity` events. In parallel test execution, use distinct session IDs for isolation.
