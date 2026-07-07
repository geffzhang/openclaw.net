# Tool Declaration Reduction

Tool declaration reduction limits which function and tool schemas are sent to the model before each model call. It is different from TokenJuice: TokenJuice reduces tool results after execution, while declaration reduction reduces tool schemas before model invocation.

The default configuration is backward compatible. Reduction is available but disabled unless `Tooling:DeclarationReduction:Enabled` is set to `true`.

## Recommended Rule Mode Configuration

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
| --- | --- |
| `Enabled` | `false` |
| `Mode` | `rule` |
| `MaxTools` | `16` |
| `MinTools` | `4` |
| `HardMaxTools` | `24` |
| `MinScore` | `0.10` |
| `FallbackToPresetOnEmpty` | `true` |
| `FallbackToRuleWhenSemanticUnavailable` | `true` |
| `EnablePromptDistillation` | `false` |

`MaxTools=16` is the default cap because OpenClaw.NET commonly runs with a large tool catalog. It keeps the pre-LLM schema payload materially smaller than sending the full preset-allowed list, while still leaving room for pinned tools, backfill, and routing probes.

## Permission Boundaries

Reduction never widens tool access. `RouteToolsDisabled`, `Session.RouteAllowedTools`, resolved presets, approval, sandboxing, and governance remain authoritative. The reducer only ranks and caps the already-allowed candidate declarations.

If a reducer returns no tools or throws, the executor fails open to the existing allowed candidate set so default safety and compatibility are preserved.

## Runtime Coverage

Native `AgentRuntime` and `MafAgentRuntime` both use the shared `OpenClawToolExecutor` declaration selection path. The same session, prompt, preset, and governance inputs therefore produce the same reduced declaration set across both orchestrators.

Turn-routing probes also use the same reduction-aware path, so routing decisions can be made against the same narrowed declaration surface that the later model turn will observe.

## Modes

- `rule`: AOT-safe deterministic scoring over tool names, descriptions, and schema text.
- `semantic`: JIT-only semantic scoring from the optional semantic reducer plugin.
- `hybrid`: Combines lexical matching and semantic similarity.
- `off`: Leaves declaration reduction configured but bypassed.

## NativeAOT and Semantic Mode

Rule mode is AOT-safe and does not require embedding or local LLM dependencies. Semantic and hybrid modes are provided by `OpenClaw.Plugins.ToolDeclarationReduction.Semantic`, a JIT-only OpenClaw plugin.

The semantic plugin uses an OpenClaw-owned implementation of tool indexing, prompt intent distillation, and hybrid search. It remains self-contained and does not depend on external embedding or local LLM packages.

To enable semantic mode, load the semantic reducer plugin through the native dynamic plugin system and set:

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

Use `Mode="hybrid"` to combine deterministic lexical scoring with semantic vector scoring. If semantic or hybrid mode is requested but the plugin is missing, OpenClaw falls back to the rule reducer when `FallbackToRuleWhenSemanticUnavailable=true`.