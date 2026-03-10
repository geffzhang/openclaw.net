# Microsoft Agent Framework Interop

OpenClaw.NET can host `Microsoft Agent Framework` (MAF) code behind OpenClaw's tool execution model.

This integration is intentionally optional:
- `src/OpenClaw.Gateway` remains MAF-free and NativeAOT-friendly.
- MAF support lives in `src/OpenClaw.MicrosoftAgentFrameworkAdapter`.

## What this adapter provides

- A normalized runner boundary (`IMicrosoftAgentFrameworkRunner`) that your host implements using MAF primitives (for example `ChatAgent` and graph orchestration).
- A single OpenClaw tool (`microsoft_agent_framework`) that routes requests to your runner.
- Configuration options for:
  - agent allowlist (`AllowedAgents`)
  - payload bounds (`MaxInputLength`)
  - response shape (`text` or `json`)

## Request contract

Tool arguments:

```json
{
  "agent": "planner",
  "input": "Summarize this incident thread",
  "thread_id": "optional-thread-id",
  "context": {},
  "format": "text"
}
```

## Host wiring (conceptual)

1. Implement `IMicrosoftAgentFrameworkRunner` in your host process.
2. Register adapter options with `AddMicrosoftAgentFrameworkInterop(...)`.
3. Create and append `MicrosoftAgentFrameworkEntrypointTool` to your OpenClaw tool list.

This keeps OpenClaw in control of gateway security and tool governance while allowing MAF to own multi-agent orchestration internals.
