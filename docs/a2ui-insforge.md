# A2UI and InsForge integration

OpenClaw.NET can expose an optional A2UI channel and optional InsForge tools for data-driven declarative UI flows.

## Configuration

Both features are off by default.

```json
{
  "OpenClaw": {
    "Channels": {
      "A2UI": {
        "Enabled": true,
        "EndpointPath": "/a2ui/stream",
        "AllowedComponentTypes": [ "form", "list", "button", "text" ]
      }
    },
    "InsForge": {
      "Enabled": true,
      "Endpoint": "https://your-insforge.example",
      "ApiKeyRef": "env:INSFORGE_API_KEY",
      "RealtimeUrl": "wss://your-insforge.example/realtime"
    }
  }
}
```

Environment overrides:

- `A2UI_CHANNEL_ENABLED=true`
- `INSFORGE_ENABLED=true`
- `INSFORGE_ENDPOINT=https://...`
- `INSFORGE_REALTIME_URL=wss://...`
- `INSFORGE_API_KEY=...`

## A2UI channel

The A2UI WebSocket endpoint defaults to `/a2ui/stream`. It uses the same gateway WebSocket request validation path as `/ws`: origin checks, public-bind token checks, and per-IP rate limiting still apply.

Server messages are JSON Lines A2UI instructions:

- `createSurface`
- `updateDataModel`
- `updateComponents`

`updateDataModel` paths must be safe RFC 6901 JSON Pointers. `updateComponents` can be constrained with `AllowedComponentTypes` so the gateway only streams approved component dictionary entries.

Client interaction events are converted into normal OpenClaw inbound messages on the `a2ui` channel.

## InsForge tools

When `OpenClaw:InsForge:Enabled` is true, the runtime registers:

- `insforge_query_component`
- `insforge_update_datamodel`
- `insforge_call_edge_a2ui`

The API key is resolved through the shared secret-ref resolver. Prefer `env:INSFORGE_API_KEY`; do not store raw production tokens in configuration.

## Realtime bridge

If `InsForge.RealtimeUrl` is set and both InsForge and A2UI are enabled, the gateway starts a background bridge. Realtime records are translated into A2UI `updateDataModel` messages and sent to the matching A2UI client by `recipientId` or `sessionId`.

## AOT and dependency notes

The implementation uses existing .NET networking primitives and source-generated JSON metadata for protocol models. It does not add reflection-heavy dependencies, and the feature remains optional for NativeAOT deployments.
