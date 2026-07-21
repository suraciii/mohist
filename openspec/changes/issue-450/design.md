## Context

Issue #450 delivers only the direct Workflow path described by `docs/actions/pi.md` and `design/runtimes/pi.md`. The Runner currently owns one `OpenCodeRuntime`; `RunnerHost`, `WorkExecutor`, `ActionContext`, promise projection, cleanup, Workflow Session routes, task classification, and the Web milestone classifier contain OpenCode-specific assumptions.

The durable AgentSession model is already the authority for logical identity, physical runtime binding, work directory, runtime lineage, transcript, model, and usage. Its Workflow runtime-event route rejects a stale `runtimeSessionId`. Pi therefore needs to connect to those contracts, not add a second event-store or ownership protocol.

Pi runs in the Runner process. Its SDK event source is an in-process callback and the completed message list is available after `session.prompt()` resolves. If the Runner dies in the submission window, Workflow redelivery can repeat a turn; the settled design accepts that limitation and forbids automatic prompt replay by `PiRuntime`.

## Goals / Non-Goals

**Goals:**

- Ship a pinned, in-process `PiRuntime` with deterministic readiness, trust, Session, completion, deadline, event, and provider-error behavior.
- Make `mohist/pi` usable through the same Action contract in Workflow tasks and checks.
- Persist and reuse Pi physical bindings before prompt submission.
- Serialize complete Workflow turns by logical Session across OpenCode and Pi inside one Runner process.
- Populate the existing AgentSession audit model with Pi text, reasoning, tools, model, usage, and cost facts.

**Non-Goals:**

- Pi execution for Mohist Agent/AgentJob work.
- Follow-up, Compact, Reset, or Cancel routing to Pi; registered Pi bindings report these commands unavailable in this issue. The existing Reset fallback for unknown historical runtime values remains unchanged.
- Runtime-aware model catalog APIs or Web model selection.
- ACP/RPC, a child process, a generic `AgentRuntime` abstraction, durable Runner event replay, Action-stream inventory, transport authentication, or credential management.

## Decisions

### D1. Pin and verify the SDK before product implementation

Add an exact `@earendil-works/pi-coding-agent` dependency to the Runner with the workspace npm command and update the lockfile deliberately. Raise the root and Runner Node engine floor to `>=22.19`, and align CI and Docker with a concrete compatible release.

Before T-002, run a real-SDK smoke and record only versions, operation names, boolean results, and sanitized field/type summaries in `sdk-smoke-verification.json`. Verify service construction and catalog loading, project-untrusted resources, Session create/open and absolute `sessionFile`, agent-session construction, literal `prompt()` completion and messages, subscriptions and stable identities, model/thinking setters, `steer`, `abort`/stop confirmation, and headless tool execution without an approval wait. If the pinned API differs from the call map in `design/runtimes/pi.md`, update that design before implementing against it. Server binding and Runner coordinator tasks are SDK-independent and may proceed in parallel; no code importing or constructing Pi starts before this gate.

### D2. Keep Pi behind one deep Runner module

Create `packages/runner/src/runtime/pi/`. All Pi SDK imports and event shapes remain inside it. Callers use Mohist-owned request, result, event, diagnostic, and error types.

One `PiRuntime` belongs to each `RunnerHost`. It owns SDK services and a cache of active SDK sessions keyed by normalized absolute session-file path. A cache miss restores through `SessionManager.open(path)`; a missing or corrupt bound file returns `missing-session` and never creates replacement context. Shutdown unsubscribes and disposes cached instances.

The SDK factory fixes project trust to false. Runner-user global configuration and provider authentication remain Pi-owned, and repository `AGENTS.md` / `CLAUDE.md` remain model context, but repository `.pi/` settings, extensions, packages, skills, and prompts do not enter execution. Mohist-owned types whitelist fields, and SDK/provider text passes the existing credential masker before logs, diagnostics, or runtime events; injected sentinel credentials never enter Action output, registration, Session facts, or the smoke artifact. Prompts, including slash-prefixed text, are submitted literally. Pi tools execute headlessly with no Mohist approval callback.

`runTurn` applies optional model and thinking level to the current physical Session and awaits `session.prompt(text)` as the sole completion authority. It reconciles the final assistant text from `session.messages`; events never complete the turn and there is no second wait. Omitted model or variant preserves Pi's restored/default behavior, and model changes do not rotate the binding.

The Workflow host declares the fixed 60-minute duration through Runner-private context; Action input cannot override it. The runtime uses an injected clock and timer seam, emits exactly one wrap-up `steer` at minute 55 while a turn is still running, fixes timeout/interruption before calling `abort`, and cannot let a late prompt completion change the result. A shorter declared runtime duration emits the warning immediately when its duration is at most five minutes. It never replays a prompt with uncertain submission state.

Extract the existing OpenCode provider-failure defaults into a runtime-neutral pure policy (`quota`/`credit`/`billing` patterns and threshold 5). The Runner composition root parses `MOHIST_PROVIDER_ERROR_PATTERNS` as an optional JSON array of case-insensitive regex sources and `MOHIST_PROVIDER_RETRY_THRESHOLD` as an optional positive integer, validates them once at startup, appends configured patterns to the defaults, freezes one combined policy, and passes that same instance to OpenCode and Pi. Invalid configuration fails before work claim with an actionable diagnostic; `docs/self-host.md` owns the operator-facing setting contract. Tests may inject a policy directly; Action input cannot configure it. Provider exhaustion normalizes to `turn-failed`; the current binding remains unchanged.

### D3. Gate new work on Pi readiness

`RunnerHost` starts both OpenCode and Pi before polling. Pi is ready after SDK services initialize and catalog loading succeeds. An empty catalog produces a warning but is ready; initialization or catalog failure keeps the Runner from claiming new work and is retried through the existing readiness loop. Already acknowledged work continues to drain. Pi models are not added to the current OpenCode-only registration payload.

### D4. Reuse AgentSession as the binding authority

Make only the Workflow Session open/attach requests runtime-aware. Open requires `runtime: "opencode" | "pi"`. Attach carries the target runtime and the binding state observed by open so the AgentSession grain can apply one guarded transition. The Server wire change and existing `mohist/opencode` caller migration land atomically; tests cover OpenCode reuse and both runtime-switch directions. The transition has four outcomes:

- first physical attach succeeds;
- the same runtime and physical ID is idempotent;
- an expected cross-runtime change replaces the physical binding and appends lineage;
- a stale observed binding or same-runtime different physical ID is rejected.

The logical AgentSession ID, source metadata, and work directory remain authoritative. A work-directory mismatch is rejected before create or prompt. `AgentSessionGrain` recognizes `pi`, while generic AgentJob routes remain OpenCode-only. Registered Pi Session commands fail as unavailable without acquiring a command reservation; Reset's current fallback for an unregistered historical runtime is preserved.

For a first Pi turn, the Action opens the logical Session, creates the physical Pi Session, persists its absolute session-file path through guarded attach, and only then reports input and submits the prompt. A failed attach prevents submission. Same-name turns, retries, model changes, cleanup, and Runner restart reuse the stored path. A missing bound file fails with Reset guidance.

An absolute Pi path is not a bounded external identifier. Remove the 256-character model limits from both persisted physical-ID copies (`AgentSessions.AgentSessionId` and transcript-turn `RuntimeSessionId`) through the normal EF migration/model snapshot path. Keep the existing indexes and API fields; do not encode, hash, truncate, or split the SDK restoration key. Server coverage carries a path longer than 256 characters through attach, runtime-event persistence, transcript read, and grain reactivation.

### D5. Serialize complete Workflow turns in process

Add one `WorkflowSessionTurnCoordinator` owned by `RunnerHost`, keyed by `(projectId, workflowRunId, sessionName)`. Task/check hosts and both adapters use one runtime-neutral Session-name normalization helper so the lease and binding resolve the same identity. The task host acquires it around the Action invocation and any worktree-cleanup reinvocation. The check host acquires the same coordinator around each Action invocation. The key is independent of runtime, so changing between OpenCode and Pi cannot overlap turns for one logical Session; different keys remain concurrent.

The coordinator is a keyed promise queue with tail cleanup. It stores no runtime binding, event, command, credential, or recovery state and makes no cross-process guarantee. Workflow redelivery after Runner death remains governed by the existing work protocol.

### D6. Implement `mohist/pi` as an Action adapter

Add a standalone adapter and register it in the existing registry. Reuse only runtime-neutral prompt loading and logical Session-name helpers. The adapter:

1. resolves a non-empty prompt and optional parent-issue context;
2. validates a fail-closed top-level input allowlist of `prompt`, `session`, and `options` after the engine consumes reserved `working-directory`; consumes `options.model` / `options.variant` while ignoring other option keys with sanitized diagnostics;
3. defaults omitted or null `session` to Work ID;
4. opens/restores and, when needed, creates and binds the Pi physical Session through D4;
5. obtains explicit acceptance for `session.input`, invokes `PiRuntime.runTurn`, reports final facts and `session.closed`, and returns the final text only as private `turnFact`.

Any undeclared top-level key, including `timeout`, `deadline`, `agent`, `kind`, or `type`, returns `invalid-input` before Session creation or Prompt submission; unknown keys nested inside `options` remain diagnostic-only. The adapter does not read a hidden `vars.agent`, interpret `expect`, inspect files, or synthesize public promise output. Task and check hosts call the same handler. `WorkExecutor` evaluates `_output`, `expect`, artifacts, and recovery only after Action success, then projects task output as `null | { promise }`; the check host maps the same Action success/failure to pass/fail without interpreting Pi. Cleanup reinvokes the already-resolved Action under the same coordinator key and binding.

Stable Action mappings include `unavailable-runtime -> runtime-unavailable`, `missing-session -> runtime-session-missing`, and `deadline-exceeded -> timeout`, plus `invalid-input`, `session-workspace-mismatch`, `session-binding-failed`, `incompatible-runtime`, `interrupted`, `turn-failed`, and `session-reporting-failed`.

### D7. Report through the existing AgentSession event path

The Pi projector normalizes assistant text, reasoning, tool lifecycle/results, model, status, automatic compaction, provider retries, and usage. Usage includes input, output, cache read, cache write, thought when supplied, cost amount, and currency. Stable Pi message IDs and tool-call IDs make duplicate callbacks idempotent; after prompt completion, final messages reconcile any missing final assistant/tool/model/usage facts. Pi compaction events map to the existing `compaction_event` audit shape. Pi retry start/end map to a runtime-neutral `provider.retry` status fact with phase, attempt, maximum attempts, delay, and masked provider message. Neither fact participates in Workflow completion.

The Action sends non-empty normalized batches through `workflowAgentSessionRuntimeEvents` with the current physical `runtimeSessionId`. The route already returns the accepted runtime-event entries: change `ServerConnection` to parse that response and require one accepted entry per submitted fact. HTTP failure, malformed response, or an empty/count-mismatched response (including the existing stale-binding result) is `session-reporting-failed`. The Server's binding check still rejects stale facts before state mutation. Add `cachedWriteTokens` and `provider.retry` through Session runtime-event parsing, transcript/read models, and shared Web types; existing cost and compaction fields are reused.

AgentSession acceptance is the Action boundary; it is deliberately not a synchronous database-flush receipt. AgentSession keeps ownership of its existing activation-memory buffering, persistence timer, and persistence retry. A later store failure does not retroactively change Workflow work. This preserves the current Session architecture without pretending that an HTTP response proves a database commit.

The reporter may buffer only the current in-memory turn so callback delivery remains ordered. Input acceptance is required before prompt submission. After any submitted prompt outcome, it sends reconciled final facts plus one `session.closed` event (`completed` for success, `failed` with the stable runtime error code for timeout/interruption/provider/runtime failure). Terminal reporting uses a dedicated 30-second, fake-timer-compatible signal that is not the already-aborted turn signal. Successful turns require terminal acceptance before completion evaluation; failure turns retain the original runtime error after terminal acceptance. If terminal reporting also fails, the original runtime error remains primary and carries a sanitized `session-reporting-failed` diagnostic; Mohist does not claim that the Session became terminal.

There is no local durable outbox, background success, cursor, stream manifest, inventory, checkpoint, replay, synchronous store-flush mode, or special transport ownership scheme. A later Workflow redelivery can repeat the turn, which is the accepted crash/reporting failure tradeoff.

AgentSession facts are audit data and never advance Workflow state. Only the normal Action result/report path completes task or check work.

### D8. Keep Server and Web changes narrow

Replace OpenCode-only Workflow classification checks with the explicit Inline Agent set `{mohist/opencode, mohist/pi}` in task classification, dispatch/YAML translation, parent-issue context, promise projection, cleanup recognition, and Web milestones. Do not infer Inline Agent behavior from substrings. Until issue #447 lands the manifest `agent-turn` capability, #450 may extend the current runtime-bearing `ActionContext` and name-based projection/cleanup seams as an explicit temporary gap; it does not present those seams as target Action architecture or add a new abstraction.

Existing runtime-neutral Session transcript, tool, status, compaction, model, usage, and lineage components render Pi facts. Add fixtures for Pi retry/compaction and cache-write/cost usage; do not add a Pi-specific view, reporting-diagnostic field, model selector, provider form, or Session-command control.

## Risks / Trade-offs

- **SDK drift:** exact pinning and a checked smoke artifact precede product code; all SDK imports remain in one module.
- **Global readiness:** a broken bundled Pi runtime pauses new work even for OpenCode, matching the settled all-capabilities readiness rule; an empty credential-backed catalog is explicitly non-fatal.
- **Crash/reporting window:** AgentSession acceptance is observable but its store flush remains asynchronous. Process death or required reporting failure can lead Workflow redelivery to repeat a submitted turn, and an AgentSession persistence retry does not retroactively fail completed work. These are explicit trade-offs and preferable to inventing ownership and recovery infrastructure in this issue.
- **Cross-Runner concurrency:** the coordinator is process-local. Current Workflow ownership routes a run to its bound Runner; distributed Session locking belongs to the broader Session-command/ownership design, not this Action slice.
- **Rollback:** an older Runner cannot execute `mohist/pi`; persisted Pi bindings remain queryable but unavailable until a Pi-capable Runner returns.

## Migration Plan

1. Pin and smoke the Pi SDK, update Node manifests/toolchains, and reconcile verified API drift.
2. Implement and fake-test `runtime/pi` plus host readiness.
3. Add runtime-aware Workflow Session binding, unbounded physical-ID persistence, explicit event acceptance, provider-retry audit, and cache-write usage contracts with Server tests.
4. Add the shared process-local coordinator to task/check hosts and cover OpenCode/Pi serialization.
5. Register `mohist/pi`, connect binding/reporting/completion/cleanup, and exercise the full fake-backed Workflow path.
6. Update narrow Web classification/presentation and implementation-gap footnotes, then run Runner, Server, and Web verification without leaving generated changes.

One narrow EF migration removes the two 256-character physical-ID limits; no data rewrite, encoding migration, or Action-stream migration is required. Deploy Server and Runner together because the new Runner sends runtime-aware Workflow Session requests and validates the runtime-event response.

## Open Questions

Only SDK-shape questions remain: the exact package version, construction options that prove project-untrusted loading, stable event identities, absolute session-file availability, and stop confirmation signal. T-001 resolves them from the real SDK and records the result before T-002. Product scope and recovery semantics are closed.
