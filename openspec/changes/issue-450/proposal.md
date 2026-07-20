## Why

Workflows can currently delegate Inline Agent tasks only to OpenCode, so teams that use Pi cannot select it as an execution backend without an unsupported external bridge. Pi now has a settled product and runtime contract, making it possible to ship it as a Runner-owned capability while preserving the Workflow completion and AgentSession behavior users already rely on.

## What Changes

- Add `mohist/pi` as a Workflow Action with `prompt`, optional logical `session`, and optional `options.model` / `options.variant` inputs. Its final assistant text participates in the existing `expect`, promise, artifact, failure, and recovery evaluation, while public output remains `null` or `{ promise }` exactly like `mohist/opencode`.
- Bundle a pinned Pi SDK with the Runner and require Pi readiness before the Runner claims work. Provider credentials remain operator-managed through Pi; Mohist does not collect or persist API keys.
- Execute Pi turns in-process with per-turn model and reasoning-level selection, a single authoritative completion signal, deadline interruption, explicit provider quota/billing failure, and no automatic replay of a prompt whose submission outcome is uncertain. A physical Session whose interruption cannot be confirmed is quarantined from later work until stop is observed or Runner restart ends the in-process turn.
- Create and durably bind a physical Pi Session before the first prompt, then reuse it for the same WorkflowRun/session name across tasks, retries, model changes, and Runner restarts. Reject workspace mismatches and missing bound session files explicitly rather than silently creating replacement context.
- Project Pi assistant text, tool activity, model observations, and token usage into the existing AgentSession transcript and usage views through an ordered durable Runner outbox and an idempotent Server cursor, so transport loss never requires replaying the Prompt and ambiguous delivery cannot double-count facts.
- Keep repository-local Pi configuration untrusted so `.pi/` settings, extensions, skills, and prompts cannot alter unattended Runner behavior; repository instruction files continue to provide model context.
- **BREAKING**: Raise the Runner's minimum Node.js version from 22.0 to 22.19, as required by the bundled Pi SDK.
- Limit this change to direct Workflow execution. Pi routing for Mohist Agent jobs, Session Follow-up/Compact/Reset/Cancel commands, and runtime-aware model catalog/UI selection remain outside this issue.

## Capabilities

- `pi-runtime`: The Runner-owned, pinned Pi execution capability covering startup readiness, trusted configuration boundaries, physical Session create/resume, in-process turn execution, model and reasoning-level application, event normalization, deadlines, quarantine after unconfirmed interruption, and provider failure semantics with validated Runner configuration.
- `pi-workflow-action`: The `mohist/pi` Workflow Action contract covering input validation and expansion, Inline Agent classification, final-text completion evaluation, stable error codes, promise output projection, cleanup follow-up reuse, and parity with existing Workflow task failure/recovery behavior.
- `pi-workflow-session`: Workflow-origin AgentSession continuity and audit behavior covering durable Pi bindings, guarded runtime switching, same-name reuse across tasks/retries/restarts, workspace and missing-file rejection, single-turn serialization, durable/idempotent event delivery, and projection of transcript, tool, model, and usage facts to existing Session views.

## Impact

- **Runner** (`packages/runner`): adds the pinned `@earendil-works/pi-coding-agent` dependency, a Pi runtime module, validated provider-policy startup options, and a durable Workflow Session event outbox; extends host readiness, Action context/registry, Workflow execution, promise projection, cleanup follow-up, runtime event reporting, and test fakes. The package Node engine moves to `>=22.19`.
- **Server** (`packages/server`): Workflow Action validation and UserFacing task classification recognise `mohist/pi`; Runner-facing Workflow AgentSession open/bind contracts carry the selected and expected-current runtime instead of assuming OpenCode, the existing OpenCode caller migrates atomically, the Session runtime registry accepts `pi`, and current bindings persist an idempotent runtime-event stream cursor.
- **Web** (`packages/web`): Workflow task/session classification recognises `mohist/pi`; existing runtime-neutral Session transcript, tool, and usage surfaces display Pi facts without introducing model-selection UI.
- **APIs and persisted state**: existing AgentSession `runtime`, `runtimeSessionId`, immutable `workDir`, and lineage fields are reused; for Pi, `runtimeSessionId` stores the absolute Pi session-file path. Workflow binding requests gain expected-current fields, runtime-event requests gain stream sequence, and Runner-local outbox state plus the Server's current-binding event cursor are durable technical state. No new user-facing Action output fields or credential APIs are introduced.
- **Developer/operator configuration**: `CONTRIBUTING.md`, CI, Docker, and package engines move to Node 22.19. Runner startup accepts a positive provider retry threshold and additional literal non-recoverable terms; invalid configuration blocks Pi readiness with diagnostics.
- **Verification**: implementation begins by pinning and smoke-verifying the real Pi SDK call/event surface, then uses fake SDK/runtime boundaries for deterministic Runner and Server tests with no real provider, network, filesystem, process, or wall clock.
