# Review: issue-560

## Verdict
FAIL. Three must-fix problems remain in the current implementation.

## Must-Fix Findings

### MF-6: Preflight scope checking breaks idempotent task replay

Violates `agent-task-launch` idempotent replay: replaying the same key and request must return the original accepted outcome. In `packages/server/src/Mohist.Server/Api/AgentTaskRoutes.cs:229-248`, the route validates `X-Mohist-Agent-Preflight` against the current Project default before calling `ResumeIdempotentAsync` at `:255`. A task accepted with default A and preflight fingerprint A will therefore return `409 launch_scope_changed` if the Project default changes to B before an identical HTTP retry, instead of returning the recorded Agent/Job/Session/Input/Turn identities. Scope drift should be rejected only when no existing launch outcome can be resumed; an existing plan must take the replay path first. No test covers an accepted replay with the original preflight fingerprint after a default change.

### MF-7: Editing an existing default-resolved Agent materializes an untouched default

Violates design D6's explicit rule not to auto-fill Agent definitions at edit time, and changes the definition-first flow. `packages/web/src/entities/agent/api/client.ts:168-177` makes `readAgentModelAndVariant` prefer the server's effective, Project-default-resolved configuration. `packages/web/src/widgets/agent-profile-editor/ui/AgentProfileEditor.tsx:60-68` uses that effective value to initialize the editable model/runtime, and `:95` writes it back on every save. Thus an existing Agent with raw `{}` plus a Project default of `pi/provider/model` becomes explicitly `{runtime: "pi", model: "provider/model"}` when the user only edits its description. Later Project-default changes no longer apply, and a definition value the user never selected has been persisted. The editor must preserve raw definition fields unless the user changes execution configuration; add a save test for a default-resolved Agent with an unrelated edit.

### MF-8: `mo agent start --epic` always fails server binding

Violates the CLI requirement that `--epic` be accepted as one of the launch context flags and forwarded as usable context. `packages/cli/Mohist.Cli/MohistCliCommands.Agent.Start.cs:27` declares the option as a string and `:119` calls the shared `BuildLaunchContext`; `packages/cli/Mohist.Cli/MohistCliCommands.Agent.cs:1014-1025` serializes that value as `context.epicNumber` JSON string. The server binder accepts only a JSON number (`packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs:742-748`, used for `EpicNumber` at `:754`), so `mo agent start --epic 7` is rejected during preflight with `validation_failed` before an Agent is created. The current CLI test checks only the outgoing mock body and does not exercise the server binder; use an integer option or validate/convert the value before serialization and cover the end-to-end type.

## Prior Finding Dispositions

- **MF-1 (effective execution projection): fixed.** `AgentInfo` and `AgentQuerier` now expose `effectiveExecutionConfig`, and the Web list/detail plus CLI Agent rendering consume it. The default-resolved and materialized-Pi read paths are covered by the current tests.
- **MF-2 (pre-launch scope confirmation): fixed in the normal flows.** Task and existing-Agent preflight routes, the Web confirmation dialog, and CLI table confirmation now show the resolved scope and send a fingerprint. MF-6 is a replay regression in that added gate.
- **MF-3 (default mutation during crash adoption): fixed.** Task-first definition facts are persisted on the Agent, and the adopted branch at `AgentTaskRoutes.cs:381-406` uses those facts without re-running the factory or reading mutable defaults.
- **MF-4 (concurrent deterministic creation): fixed.** `AgentGrain.CreateAsync` is first-writer-wins and returns/adopts only matching task-first fingerprints; `AgentGrainSpecs` covers matching and conflicting adoption.
- **MF-5 (collaborators and concurrency intent in Web): fixed.** The task request, Web composer controls, profile editor, and Agent detail projection now carry and display the collaborator and concurrency fields.

## Review Dimensions

- Acceptance criteria reread before reviewing the current code: checked against the issue artifacts and the existing review's issue-level criteria.
- Coverage: **FAIL** because replay stability, non-materializing refinement, and the advertised CLI epic context are incomplete.
- Correctness: **FAIL** for MF-6 through MF-8; the core task-first, default, rollback, and first-writer paths otherwise behave consistently with their criteria.
- Consistency with surrounding code and plan: **FAIL** because MF-7 contradicts design D6 and MF-8 contradicts the server's established typed context contract.
- Tests: **FAIL for completeness.** CLI tests 1,860/1,860, Web focused tests 121/121, Server unit tests 2,701/2,701, and Server specifications 3,952/3,952 pass, but none covers the three failing cases above.

## Observations

- `agent-task-launch/spec.md` still documents exactly seven task fields, while the current route intentionally accepts `allowedSubagentAgentIds` and `maxConcurrentRuns` for the collaborator/concurrency requirement. The spec artifact should be synchronized with the implemented contract.
- The full repository `npm run verify` gate was not rerun in this review. Server build, focused Web typecheck, format validation, CLI tests, Web tests, Server unit tests, and the full Server specification assembly were run successfully.

<promise>FAIL</promise>
