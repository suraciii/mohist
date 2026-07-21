## Why

Mohist currently discovers models through OpenCode's SDK v2 catalog, which does not derive reasoning variants, so every model selector loses the reasoning-strength choices that the OpenCode CLI can report. Model discovery is also a hard runtime-readiness prerequisite, allowing an auxiliary configuration hint to stop the runner from claiming otherwise executable work.

## What Changes

- Discover OpenCode models with `opencode models --verbose` and derive each model's variant list from the keys of its `variants` object, restoring model-and-reasoning-strength choices in all existing selectors.
- Run CLI discovery once during runner startup and thereafter on the existing configurable rediscovery interval; every discovery invocation executes the command and reads its complete output without a time-based cache.
- Preserve the existing command override precedence: the models-specific command, then the general agent command, then `opencode`.
- Keep model discovery best-effort: an initial failure reports an empty catalog, while a failed or empty rediscovery preserves the last non-empty catalog and retries on the next interval.
- Continue sending an immediate heartbeat only when the discovered models or variants change, using the existing registration fields and server aggregation path.
- Define OpenCode runtime readiness solely from the shared server lifecycle and health; model discovery failure no longer blocks work claiming.
- **BREAKING (runner-internal)** Remove SDK catalog loading and refresh from the `OpenCodeRuntime` readiness lifecycle and catalog API. No runner-to-server or user-facing API contract changes.

## Capabilities

- `opencode-model-catalog`: Discover the available OpenCode model IDs and per-model reasoning variants from complete `opencode models --verbose` output, including command selection and parse/failure behavior.
- `runner-model-discovery`: Perform startup and periodic best-effort discovery, preserve the last non-empty catalog on refresh failure, and propagate changed models and variants through the existing heartbeat contract.
- `opencode-runtime`: Determine runtime readiness from the OpenCode server's lifecycle and health independently of model discovery, so discovery failures do not suspend work claiming.

## Impact

- **Runner model discovery** (`packages/runner/src/runtime/`): restore a CLI-backed discovery boundary and verbose-output parser, including synchronous full-stdout capture and test seams; retire SDK v2 catalog discovery.
- **Runner runtime and host** (`packages/runner/src/runtime/opencode/`, `packages/runner/src/runtime/host.ts`): decouple catalog state and refresh from `OpenCodeRuntime`, initialize discovery independently, and retain the existing periodic refresh, change comparison, immediate heartbeat, and timer cleanup behavior.
- **Runner configuration**: retain `MOHIST_AGENT_MODELS_COMMAND` → `MOHIST_AGENT_COMMAND` → `opencode` command precedence and the configurable rediscovery interval with its 30-minute default and 60-second minimum.
- **Tests** (`packages/runner/tests/`): update runtime readiness and host lifecycle coverage; restore parser/command tests for variants, malformed output, full stdout, failures, and no-cache behavior without invoking real processes or wall-clock time.
- **Server, Web, and CLI APIs**: unchanged. Existing `coderModels` / `coderModelVariants` registration fields, server aggregation and model endpoint, selector chips, selection persistence, and clearing behavior remain the consumers of the corrected runner data.
- **Dependencies and persistence**: no new dependency, database migration, persisted-schema change, or OpenCode upstream modification.
