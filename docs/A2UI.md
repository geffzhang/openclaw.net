# A2UI Unified Protocol

> Plan step 9 — single canonical reference for the A2UI subsystem. Replaces the previously
> separate Canvas-A2UI and InsForge-bridge documents.

OpenClaw exposes a single, version-negotiable A2UI subsystem with two complementary layers:

| Layer | Purpose | Wire form |
| --- | --- | --- |
| **v0.8 frames** | Render contract — discrete UI updates pushed to a connected Canvas. | JSONL frames over the existing `/ws` Canvas envelope (`a2ui_push`) |
| **v0.9 instructions** | State contract — `createSurface` / `updateDataModel` / `updateComponents`. | Either as `a2ui_instruction` envelopes over `/ws`, or as JSONL on the standalone `/a2ui/stream` endpoint |

The **same 11-component dictionary** (`text`, `markdown`, `card`, `button`, `input`, `select`,
`checklist`, `table`, `image`, `progress`, `chart`) underpins both layers. v0.9
`updateComponents` payloads are validated against the same allow-list as v0.8 frames.

## Capability negotiation

Clients advertise capabilities via `canvas_ready` (Canvas/`/ws`) or via the first message
on `/a2ui/stream`. Recognised capabilities:

| Capability | Meaning |
| --- | --- |
| `a2ui.v0_8` | Client renders v0.8 JSONL frames. (Canvas always supports v0.8.) |
| `a2ui.v0_9` | Client renders v0.9 instructions (`createSurface` / `updateDataModel` / `updateComponents`). |
| `a2ui.eval`  | Client is willing to run sandboxed JS via `a2ui_eval`. |
| `a2ui.data_model` | Subset of v0.9 — client accepts `updateDataModel` only. |
| `a2ui.components` | Subset of v0.9 — client accepts `updateComponents` only. |
| `a2ui.create_surface` | Subset of v0.9 — client accepts `createSurface`. |

Default-when-absent (per plan §三.1): `/ws` Canvas connections default to `v0.8`, the
standalone `/a2ui/stream` endpoint defaults to `v0.9`.

## Tools

### v0.8 (Canvas embedded — production, used by webchat / Companion)

- `a2ui_push` — push a JSONL frame batch.
- `a2ui_reset` — clear A2UI-rendered content.
- `a2ui_eval` — run a script in the local A2UI sandbox.

### v0.9 (instruction-style — added by plan step 5)

- `a2ui_create_surface` — create a v0.9 surface.
- `a2ui_update_data_model` — patch the dataModel at an RFC 6901 JSON Pointer.
- `a2ui_update_components` — replace a components subtree.

All v0.9 tools require the connected Canvas client to advertise `a2ui.v0_9`. Clients that
have not been upgraded receive a structured "not supported" rejection — there is no silent
fallback (per plan §五 risk mitigation).

## Configuration

The unified `OpenClaw:A2UI` section (plan step 6) is the recommended config root. Legacy
keys remain authoritative for at least one release; setting any field on the unified section
overrides the corresponding legacy key.

```jsonc
{
  "OpenClaw": {
    "A2UI": {
      "Enabled": true,
      "AllowOnPublicBind": false,
      "Connection": {
        "MaxConnections": 256,
        "MaxConnectionsPerIp": 16,
        "MessagesPerMinutePerConnection": 120,
        "ReceiveTimeoutSeconds": 120,
        "MaxMessageBytes": 65536
      },
      "Frames": {
        "MaxFramesPerPush": 100,
        "MaxBytes": 262144,
        "MaxInstructionBytes": 131072
      },
      "Components": {
        "AllowedTypes": [],     // empty ⇒ default 11-type dictionary
        "AllowAny": false       // true ⇒ legacy "allow all" passthrough
      },
      "Surfaces": {
        "DefaultSurface": "main",
        "AllowMultipleSurfaces": true
      },
      "InsForge": null            // when null, the legacy OpenClaw:InsForge block applies
    }
  }
}
```

### Legacy key mapping

| Unified key | Legacy key | Status |
| --- | --- | --- |
| `OpenClaw:A2UI:Enabled` | `OpenClaw:Canvas:Enabled` + `OpenClaw:Channels:A2UI:Enabled` | Both kept; unified overrides if set |
| `OpenClaw:A2UI:AllowOnPublicBind` | `OpenClaw:Canvas:AllowOnPublicBind` | Both kept; unified overrides if set |
| `OpenClaw:A2UI:Connection:*` | `OpenClaw:Channels:A2UI:Max*` / `*Seconds` / `MessagesPerMinute*` | Per-field overlay |
| `OpenClaw:A2UI:Frames:MaxFramesPerPush` | `OpenClaw:Canvas:MaxFramesPerPush` | Overlay |
| `OpenClaw:A2UI:Frames:MaxBytes` | `OpenClaw:Canvas:MaxCommandBytes` | Overlay |
| `OpenClaw:A2UI:Frames:MaxInstructionBytes` | `OpenClaw:Channels:A2UI:MaxInstructionBytes` | Overlay |
| `OpenClaw:A2UI:Components:AllowedTypes` | `OpenClaw:Channels:A2UI:AllowedComponentTypes` | Overlay (empty array ⇒ default dictionary) |
| `OpenClaw:A2UI:Components:AllowAny` | `OpenClaw:Channels:A2UI:AllowAnyComponentType` | Overlay |
| `OpenClaw:A2UI:InsForge` | `OpenClaw:InsForge` | Full block override |

When startup detects values living only on a legacy key, a single warning is logged of the
form: *"A2UI legacy configuration keys detected (X); migrate to OpenClaw:A2UI."*

## Security model

All A2UI traffic — both v0.8 frames and v0.9 instructions — shares one set of safety primitives:

- **JSON Pointer safety:** `A2UIProtocol.IsSafeJsonPointer` — RFC 6901, length-bounded, no
  control characters, validated `~0` / `~1` escapes only.
- **Component allow-list:** `ComponentTypePolicy` — recursive walk that rejects any component
  whose `type` is outside the allow-list. **Default behaviour was changed** from "empty = allow
  any" to "empty = default 11-type dictionary"; explicit `AllowAny=true` is required to
  restore the previous passthrough mode (plan step 2).
- **Per-connection rate limit:** `MessagesPerMinutePerConnection` (default 120).
- **Connection caps:** `MaxConnections` (256) and `MaxConnectionsPerIp` (16).
- **Receive byte cap:** `MaxMessageBytes` (64 KiB), enforced before parsing.
- **Instruction byte cap:** `MaxInstructionBytes` (128 KiB) for v0.9 outbound.
- **Receive timeout:** `ReceiveTimeoutSeconds` (120 s).
- **Public-bind gate:** `AllowOnPublicBind` (default `false`); the subsystem refuses to
  serve v0.8 Canvas commands and v0.9 instructions to non-loopback binds unless explicitly
  enabled.

## Endpoints

| Endpoint | Purpose | Default version |
| --- | --- | --- |
| `/ws` | First-party Canvas + chat. v0.8 frames embedded as `a2ui_push` envelopes. v0.9 instructions ride as `a2ui_instruction` envelopes when the client advertises `a2ui.v0_9`. | `v0.8` |
| `/a2ui/stream` | Standalone v0.9 instruction stream. JSONL of `A2UIInstruction` records. Default-off (`OpenClaw:A2UI:Enabled`). | `v0.9` |

## InsForge Realtime bridge

The InsForge bridge (`InsForgeRealtimeBridge`) subscribes to InsForge Realtime events and
publishes resulting `updateDataModel` instructions onto connected `/a2ui/stream` clients.

Configuration lives under either `OpenClaw:InsForge` (legacy) or
`OpenClaw:A2UI:InsForge` (unified). Required fields:

- `Endpoint` — InsForge base URL.
- `RealtimeUrl` — Realtime WebSocket URL.
- `RealtimeSubscribePayload` — initial subscribe message (raw JSON string).
- `RealtimeSessionIdProperty` / `RealtimeRecipientIdProperty` / `RealtimePathProperty` /
  `RealtimeValueProperty` — JSON pointers/property names used to extract routing info from
  inbound events.
- `RealtimeJsonPointerPrefix` — guard prefix; updates outside this prefix are rejected.

When a future PR completes plan step 7, the bridge will publish via the per-session
`A2UISession` abstraction; v0.8-only Canvas clients will then automatically receive
equivalent v0.8 frame batches in place of `updateDataModel` instructions.

## Plan history

| Step | Status | Notes |
| --- | --- | --- |
| 1. Extract A2UI protocol layer | ✅ shipped | `OpenClaw.Core/A2UI/` + thin forwarders |
| 2. Unified component dictionary | ✅ shipped | Default = 11-type whitelist; `AllowAny` opt-in |
| 3. `A2UISession` + version negotiation | ✅ shipped | Per-connection state machine |
| 4. `CanvasA2UIBridge` (v0.8 ↔ v0.9 projection) | ⏳ deferred | Highest-risk refactor; warrants own PR + test suite |
| 5. v0.9 tools | ✅ shipped | `a2ui_create_surface`, `a2ui_update_data_model`, `a2ui_update_components` |
| 6. `OpenClaw:A2UI` config consolidation | ✅ shipped | Legacy keys kept, deprecation warning at startup |
| 7. InsForge ⇒ Session | ⏳ deferred | Depends on step 4 |
| 8. Rate-limit / connection-cap tests | ✅ shipped | 7 new cases against `A2UIChannel` |
| 9. Doc consolidation | ✅ shipped | This document |
| 10. Forwarder removal | ⏳ explicit post-migration cleanup | Plan defers until all clients migrated |

## Backwards compatibility

- Existing webchat / Companion clients (which advertise neither `a2ui.v0_8` nor `a2ui.v0_9`)
  continue to receive v0.8 frames via `a2ui_push` exactly as before.
- Existing standalone `/a2ui/stream` clients continue to receive `updateDataModel` /
  `updateComponents` instructions exactly as before.
- `OpenClaw:InsForge.Enabled` + `OpenClaw:Channels:A2UI.Enabled` continue to gate the bridge
  exactly as before; the unified `OpenClaw:A2UI:Enabled` is additive.

See also: [`COMPATIBILITY.md`](../COMPATIBILITY.md) for cross-version compatibility notes.

## Appendix A: original Canvas-A2UI doc

See [`CANVAS_A2UI.md`](./CANVAS_A2UI.md) for the v0.8-focused historic document. Content
will be folded into this file when plan step 4 ships.

## Appendix B: original InsForge bridge doc

See [`a2ui-insforge.md`](./a2ui-insforge.md) for the InsForge-Realtime-focused historic
document. Content will be folded into this file when plan step 7 ships.
