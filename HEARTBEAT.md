# Heartbeat Service Configuration

The heartbeat service is a periodic background task that runs at a configurable interval.
It reads its configuration from the `OpenClaw:Heartbeat` section in `appsettings.json`
(or equivalent configuration sources).

## Configuration

| Property            | Type   | Default | Description                                                       |
|---------------------|--------|---------|-------------------------------------------------------------------|
| `Enabled`           | bool   | `false` | Whether the heartbeat service is active.                          |
| `IntervalSeconds`   | int    | `30`    | Seconds between heartbeat ticks. Minimum enforced value is **5**. |
| `LogLevel`          | string | `Debug` | Log level for heartbeat ticks (`Debug`, `Information`, …).        |

## Example (`appsettings.json`)

```json
{
  "OpenClaw": {
    "Heartbeat": {
      "Enabled": true,
      "IntervalSeconds": 30,
      "LogLevel": "Debug"
    }
  }
}
```

## Behaviour

* **Async loop** – the service derives from `BackgroundService` and uses `PeriodicTimer`
  with `CancellationToken` for cooperative shutdown.
* **No re-entrance** – a `SemaphoreSlim(1,1)` gate ensures that a slow heartbeat tick
  cannot overlap with the next one; the overlapping tick is skipped and a warning is logged.
* **Graceful stop** – `CancellationToken` propagated from the host signals the loop to
  exit without throwing.
