## Why

Named Agents currently expose `model` and `variant`, but `variant` is overloaded as an informal proxy for reasoning strength. The existing configuration, readiness, and launch surfaces cannot tell users which reasoning effort will actually run or why a model/effort combination cannot execute; making reasoning effort explicit now prevents that ambiguity from spreading across Agent entry points and Job snapshots.

## What Changes

- Add `reasoningEffort` to the Agent-owned execution configuration with a stable, bounded vocabulary spanning no reasoning effort through the highest effort. Define clear handling for absent, empty, and unknown values.
- Keep `variant` as a separate runtime-specific setting that represents a real provider/runtime variant. **BREAKING:** existing Agent configurations must no longer have `variant` silently interpreted as reasoning effort; configurations relying on that meaning must be made explicit.
- Extend Agent create/edit/read surfaces so Web, CLI, and API users can set, clear, and see the final `runtime`, `model`, `reasoningEffort`, and `variant` values. Agent list/detail, Agent Connection readiness/launch, and launch/job result surfaces must use the same vocabulary and projection.
- Publish the known runtime capabilities needed to validate reasoning effort, including whether a runtime supports it and whether the selected model supports the requested effort. Validate the complete configuration before launch and distinguish missing configuration, unsupported effort, incompatible model/effort combinations, and temporarily unknown availability.
- Resolve the Agent's final execution tuple before creating work, including Agent Connection launches, then freeze `runtime`, `model`, `reasoningEffort`, and `variant` into the AgentJob, AgentSession, and Runner dispatch snapshot. The Runner must deliver effort and variant independently, and later Agent edits must not change an accepted Job.
- Preserve the requested model and reasoning effort while they are temporarily unavailable: the Job waits or retries for the same configuration and never substitutes another model, effort, or provider fallback.
- Update the readiness, launch, waiting, and execution-result diagnostics and regression coverage so users can distinguish configuration errors from temporary execution unavailability without relying on provider probing.

## Capabilities

- `agent-reasoning-effort`: Canonical Agent-level reasoning-effort configuration, stable values and validation, independent runtime-specific variants, and consistent API, Web, CLI, list, and detail projections.
- `agent-execution-compatibility`: Known runtime/model capability information and pre-launch compatibility evaluation, including distinct readiness outcomes for missing, unsupported, incompatible, and temporarily unconfirmed configurations without active availability probing.
- `agent-job-execution-snapshot`: Resolution and durable delivery of the final runtime/model/reasoning-effort/variant tuple across Agent launch, AgentSession, AgentJob, Runner dispatch, execution results, and recovery, including snapshot immutability and no-fallback waiting.

## Impact

- **Server and persistence:** `AgentConfigSchema`, Agent execution-definition resolution, Agent Connection readiness and launch admission, AgentJob/coordinator/dispatch snapshots, AgentSession execution facts, and Agent launch/job read DTOs. The opaque Agent config and serialized snapshots gain the new execution fact while preserving append-only snapshot evolution.
- **Runner:** runtime capability/catalog registration, AgentJob option parsing, OpenCode/Pi runtime request boundaries, and execution diagnostics must carry `reasoningEffort` separately from `variant` and retain the requested tuple during temporary unavailability.
- **Web and CLI:** Agent editor controls, Agent Connection readiness views, model/effort compatibility messaging, list/detail summaries, launch observation/result views, typed Agent create/edit flags, and table/JSON output.
- **Workflow boundaries and dependencies:** the change is scoped to saved Mohist Agent execution; existing inline Workflow `options.variant` remains a runtime-facing contract unless a shared boundary needs an additive field. No provider fallback, active availability probe, or new external dependency is required.
