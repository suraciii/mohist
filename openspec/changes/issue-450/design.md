## Context

Issue #450 adds the Workflow-direct `mohist/pi` path described by the proposal and the `pi-runtime`, `pi-workflow-action`, and `pi-workflow-session` specs in this change. The product and runtime contracts are already fixed in `docs/actions/pi.md` and `design/runtimes/pi.md`; this document maps those contracts onto the current implementation.

Today the Runner has one execution backend. `RunnerHost` constructs one `OpenCodeRuntime`, gates all polling on its readiness, and injects it into `WorkExecutor`. `ActionContext` carries only `openCodeRuntime`; `actions/registry.ts`, promise projection, and worktree cleanup recognise only `mohist/opencode`. The OpenCode Action already provides the desired completion seam: it returns final assistant text through the private `turnFact`, after which `WorkExecutor` evaluates task-level completion and projects `null | { promise }`.

Workflow AgentSession infrastructure is mostly runtime-neutral, but its Runner API is not. `RunnerRoutes` hard-codes `opencode` in Workflow Session open and attach calls, while `AgentSessionGrain.IsRuntimeRegistered` recognises only OpenCode. The persisted model already has the fields Pi needs: stable logical `sessionId`, runtime, `runtimeSessionId`, immutable `workDir`, Runner identity, and append-only runtime lineage. For Pi, `runtimeSessionId` is the absolute Pi JSONL session-file path.

There is also an observability gap in the current Workflow Action path: `OpenCodeRuntime.runTurn` supports an observer and the Server accepts Workflow runtime events, but `opencodeAction` does not connect them. The current runtime-event request has no idempotency identity and usage is applied additively, so simply retrying an ambiguous POST could double-count facts. Pi must therefore add ordered durable delivery, not only connect an observer, so the existing runtime-neutral transcript and Session UI remain complete across transport loss and Runner restart.

Pi is a fast-moving 0.x SDK and requires Node >= 22.19. The implementation is therefore gated by pinning a real SDK version and smoke-verifying the exact creation, persistence, prompt, event, model, interruption, trust, and catalog surfaces before product code is written. Default tests cannot use the real SDK environment, provider network, physical Session store, process, or wall clock.

Stakeholders are Workflow users selecting `mohist/pi`, Runner operators supplying provider credentials, and Session viewers auditing the resulting conversation. Mohist Agent/AgentJob users and Session-command users are deliberately unaffected by this issue.

## Goals / Non-Goals

**Goals:**
- Add a pinned, in-process `PiRuntime` deep module with startup readiness, persistent Session restore, turn execution, event projection, deadlines, interruption confirmation, and provider-error policy.
- Add `mohist/pi` as a UserFacing Workflow Inline Agent Action while preserving existing Workflow input expansion, completion, promise projection, recovery, and cleanup behavior.
- Persist a Pi binding before the first prompt and reuse it by WorkflowRun/session name across tasks, retries, model changes, cleanup turns, and Runner restart.
- Keep project-local Pi resources untrusted while allowing Runner-user global Pi configuration, provider authentication, and repository instruction files.
- Populate the existing AgentSession transcript, tool, model, and usage views with Pi facts.
- Deliver normalized Session facts through a durable ordered outbox and idempotent Server cursor without replaying Prompt execution.
- Raise and enforce the Runner's Node floor to 22.19.

**Non-Goals:**
- Pi execution for Mohist Agent/AgentJob work or an Agent runtime selector.
- Pi Follow-up, Compact, Reset, or Cancel command routing.
- Pi model-catalog registration, Server API, or Web model-selection UI.
- ACP, Pi RPC mode, a child-process isolation boundary, or a generic `AgentRuntime` interface.
- Provider credential management, per-tool approval, sandboxing, or a configurable project-trust switch.
- Automatic prompt replay, deterministic Prompt IDs, or recovery of a turn interrupted by Runner process death.

## Decisions

### D1. Add a parallel `PiRuntime` deep module

Create `packages/runner/src/runtime/pi/` with Mohist-owned boundary types, SDK factory/services, runtime lifecycle, turn execution, event projection, error normalization, and a public `index.ts`. All imports from `@earendil-works/pi-coding-agent` and related Pi packages stay inside this directory. Callers receive only `PiSessionTarget`, `PiTurnRequest`, `PiTurnResult`, diagnostics, and stable runtime error kinds.

One `PiRuntime` instance belongs to each `RunnerHost`. It owns one `ModelRuntime`, Runner/global settings and resource-loading policy, a cache of active SDK AgentSession instances keyed by absolute session-file path, and each cached instance's unsubscribe handle and event projector. `shutdown()` unsubscribes and disposes all cached instances. The module receives already-resolved Workflow input and bindings; it never receives Agent IDs or resolves Agent definitions.

The SDK service factory fixes project trust to false for every work directory. It loads Runner-user global settings/authentication and the repository's `AGENTS.md`/`CLAUDE.md` context, but supplies empty project settings, extensions, packages, skills, and prompt resources for `.pi/`. The exact SDK options are resolved by D2's smoke; callers never choose trust or resource-loader flags.

**Rationale:** Pi lifecycle, rapidly changing SDK construction, event interpretation, and persistence semantics remain in one high-cohesion module. **Alternatives considered:** wrapping Pi and OpenCode in a new `AgentRuntime` interface was rejected because their lifecycle, identity, and command surfaces have not converged; adapting Pi through OpenCode, ACP, or RPC was rejected because it adds another protocol and changes prompt-completion semantics.

### D2. Pin and smoke-verify the SDK before implementation

Add an exact `@earendil-works/pi-coding-agent` version to `packages/runner/package.json` using the Runner workspace npm command so `package-lock.json` is updated deliberately. Set both root and Runner `engines.node` to `>=22.19`. Record a real-SDK verification at `openspec/changes/issue-450/sdk-smoke-verification.json`, following issue #409's artifact shape.

The smoke must verify at least: `ModelRuntime.create()` and available-model loading; untrusted project-resource setup; `SessionManager.create(cwd)` and `SessionManager.open(file)`; returned `sessionFile`; `createAgentSession`; `prompt()` completion and final messages; `subscribe()` event payloads for text, thinking, tools, usage, and retry; `setModel`; `setThinkingLevel`; `steer`; `abort`; and the state used to confirm interruption. It must also verify that prompt-template/slash-command expansion can be disabled. If names or payloads differ, update `design/runtimes/pi.md` and this call map before implementing.

**Rationale:** current upstream documentation confirms the broad API but the SDK's 0.x construction and event shapes move frequently. **Alternatives considered:** using a semver range was rejected because an unattended Runner cannot absorb weekly breaking changes; implementing against documentation alone was rejected because session-file timing and event payload details are acceptance-critical.

### D3. Gate work claiming on both runtimes, but do not publish Pi catalog yet

`RunnerHost.initializeSharedConnection` constructs and starts OpenCode and Pi before connecting to the Server. `WorkExecutor` receives both handles; `AgentJobExecutor` continues receiving only OpenCode. The worker loop replaces `isOpenCodeReadyForClaim` with a combined readiness check. It continues draining `awaitingAck`, but skips new polls while either runtime is unavailable and emits the failing runtime's diagnostic with the existing throttling policy.

Pi is ready after SDK services initialize and `ModelRuntime.getAvailable()` completes. An empty result stores a warning but remains ready. Initialization or catalog failure leaves Pi not-ready; the host periodically retries `start()` while polling is gated. Pi has no external server-exit watcher: Runner shutdown naturally ends in-process turns, and a new process lazily restores persisted Sessions.

The existing `coderModels` and `coderModelVariants` registration remains OpenCode-derived. Pi loads its catalog only as a readiness/compatibility check in this issue; runtime-aware catalog reporting belongs to the separate catalog/UI work.

**Rationale:** claiming only when every bundled execution capability is initialized avoids claim-then-fail behavior and matches the accepted Runner readiness contract. **Alternatives considered:** gating only when the polled item uses Pi would require claiming before readiness is known and would consume a workflow slot; merging Pi models into the unqualified OpenCode catalog was rejected because it loses runtime identity and expands this issue into UI/API design.

### D4. Implement `mohist/pi` as its own Action adapter

Add `actions/pi.ts` and register it in `createDefaultRegistry`. The adapter follows the OpenCode Action flow but does not call or wrap `opencodeAction`:

1. Resolve `prompt` through the existing prompt loader and compose parent-issue context.
2. Validate explicit `session` and `options`; preserve the raw options object long enough to diagnose unknown keys.
3. Derive the logical Session name, defaulting to Work ID.
4. Enter the per-logical-Session serialization boundary from D6.
5. Resolve/create/bind the Workflow AgentSession using D5.
6. Persist `session.input`, then invoke `PiRuntime.runTurn` with the D8 reporter.
7. Map runtime errors to Action codes and return `turnFact.finalAssistantText` on success.

Move the genuinely runtime-neutral prompt-loader/session-name helpers out of `opencode-helpers.ts` or rename that module; do not share Action input types. Add `piRuntime` to `ActionContext`, `WorkExecutor`, `baseContext`, check contexts, and cleanup contexts. Add `mohist/pi` to `PROMISE_PROJECTED_ACTIONS` and the agent-backed cleanup classifier, so the existing completion and worktree enforcement pipelines remain authoritative.

Input parsing accepts only non-empty resolved prompt text plus optional `session` and an `options` object. Omitted/null `session` uses Work ID; a present string is trimmed and must remain non-empty; whitespace-only and non-string values fail `invalid-input` before any Session creation and are never stringified. It splits `options.model` at the first slash, keeps `variant` independent, treats null as omitted, and passes unknown option names to runtime diagnostics. It never reads `vars.agent` unless the Workflow expansion explicitly placed that object in `options`. Legacy `with.agent`, `with.kind`, and `with.type` continue to fail in Server dispatch validation.

The adapter maps `unavailable-runtime` -> `runtime-unavailable`, `missing-session` -> `runtime-session-missing`, and `deadline-exceeded` -> `timeout`; it preserves `invalid-input`, `incompatible-runtime`, `interrupted`, and `turn-failed`, and owns `session-workspace-mismatch` / `session-binding-failed`. These are Action errors only. Diagnostics, provider text, model, usage, and physical identity never enter Action output.

Server validation and presentation use an explicit inline-Agent Action set containing `mohist/opencode` and `mohist/pi`. Replace OpenCode substring checks in `TaskRun.DeriveClassification`, `WorkflowItemTranslator`, `WorkflowYamlSerializer`, parent-issue-context dispatch, and the Web milestone classifier. Before changing parent-context dispatch code, update the architecture-owned `design/workflow/task-dispatch.md` rule from OpenCode-only to the explicit inline-Agent Action set; then preserve its existing plan/task/parent guards. Error messages refer to the selected Action rather than naming OpenCode.

**Rationale:** Action selection is already the Workflow runtime selector, while completion belongs to `WorkExecutor`. **Alternatives considered:** adding `options.runtime` was rejected because it creates two conflicting selectors; extracting a generic Inline Agent Action was rejected because only prompt loading, Session naming, and executor completion are stable shared knowledge.

### D5. Make Workflow Session binding runtime-aware and race-safe

Extend only the Workflow Runner Session requests with an explicit required `runtime` (`opencode` or `pi`). The Server contract and the existing `mohist/opencode` caller migrate atomically: OpenCode sends `runtime: "opencode"` on open and the observed expected-current runtime/session on bind, with regression tests preserving its current behavior. Keep generic AgentJob routes hard-coded to OpenCode in this issue. Split the shared attach DTO so AgentJob callers do not acquire Pi fields accidentally.

Add a narrow AgentSession grain command for Workflow runtime binding with:
- target runtime, physical `runtimeSessionId`, work directory, model, and Runner;
- the `expectedRuntimeSessionId` and expected runtime observed by the preceding open;
- these atomic outcomes: first attach; idempotent same-binding success; expected cross-runtime rebind in either direction with lineage append; stale/conflicting binding rejection; same-runtime different physical ID rejection with a Reset hint.

The command reuses the existing `AttachPhysicalSession` and `RebindRuntimeSession` domain transitions but is not a public Reset and does not create a Session-command reservation. The expected-current fields prevent two Actions from replacing each other's binding. Before any cross-runtime rebind, the Runner must prove the old binding's D8 outbox is empty so already-recorded facts cannot become stale after replacement. `AgentSessionGrain.IsRuntimeRegistered` adds `pi`; historical fallback remains unchanged.

The Pi Action binding sequence is:

```text
open logical Session(runtime=pi)
  -> reject immutable workDir mismatch
  -> current pi binding: restore it
  -> no binding or different runtime: create Pi Session
       -> bind with expected-current guard
       -> only after bind succeeds, submit session.input and prompt
```

For Pi, the physical ID sent to the Server is `session.sessionFile` normalized to an absolute path. The Pi UUID is diagnostic only. If creation yields no file path, return `incompatible-runtime`. If a same-runtime bound file is absent/corrupt, return `runtime-session-missing` with a Reset instruction and never create a replacement. A failed bind can leave an unused Pi file, but cannot leave a submitted prompt with no durable logical owner.

**Rationale:** the existing model already owns runtime lineage; a guarded grain command preserves that authority and closes the create/bind race. **Alternatives considered:** allowing `AttachPhysicalSessionAsync` to replace any binding was rejected because it weakens existing Reset protection; reusing the Session Reset command was rejected because runtime selection during Workflow execution is not a user Session command and must not acquire idle/reservation semantics.

### D6. Serialize the complete logical Session operation, not only `prompt()`

Add a keyed promise queue owned by the Pi Workflow adapter/coordinator, keyed by `(projectId, workflowRunId, sessionName)`. The queue covers open, create, guarded bind, input reporting, model application, and `prompt()` completion. Queue entries are removed when their tail settles. Different keys execute independently.

`PiRuntime` also protects each cached physical SDK Session against overlapping prompt calls as a defensive invariant, but the outer logical queue is authoritative because two unbound tasks have no physical path yet. Cleanup turns resolve the same key and therefore continue after the original turn on the same binding.

If abort cannot be confirmed, `PiRuntime` marks the cached physical path quarantined before returning interruption-unconfirmed. The logical queue can settle the failed TaskRun, but admission checks reject every later turn on that path with `unavailable-runtime`; other paths remain independent. A later subscribed stop observation clears quarantine. Runner process restart also clears it safely because process termination has ended every in-process Pi turn. This prevents queue release from creating physical overlap without holding one unresolved promise forever.

**Rationale:** a physical-ID-only lock cannot prevent two first turns from creating and racing different bindings, while a work-directory lock over-serializes unrelated Sessions. **Alternatives considered:** rejecting the second turn was rejected because the requirement is serialization, not failure; a distributed Server lease was rejected because Workflow dispatch already assigns work to one Runner and the invariant only coordinates in-process execution against a Runner-owned SDK object.

### D7. Keep turn policy shared as pure rules, not a shared runtime

`PiRuntime.runTurn` restores or uses the cached SDK Session, applies `setModel` and `setThinkingLevel`, subscribes before prompt admission, and calls `await session.prompt(text)` with template/slash expansion disabled. Prompt resolution is the only success authority; `agent_end` is projection input only. Final assistant text is read from the completed Session messages and returned as a private fact.

Extract only genuinely runtime-neutral policy from OpenCode into small pure modules: the task-independent five-minute warning text/window, timeout race/result fixation, default quota/credit/billing patterns, and retry threshold. OpenCode and Pi each translate native events into that policy's input and each own their abort/confirmation implementation. This avoids a generic runtime interface while keeping one authority for behavior promised as identical.

At warning time Pi uses `steer`; at deadline it first fixes `deadline-exceeded`, then calls `abort` and checks subscribed lifecycle state plus `isStreaming`. A late prompt resolution cannot change the fixed result. An external Action signal follows the same interruption cleanup but maps to `interrupted`. An unconfirmed stop produces an interruption-unconfirmed diagnostic. No failure path resubmits a prompt.

Pi `auto_retry_start` facts feed the shared provider policy. Quota/balance/billing wording fails on first occurrence; recoverable retries fail at the configured threshold (default five); lower attempts remain Pi-owned. `cli.ts` parses `MOHIST_PROVIDER_RETRY_THRESHOLD` as a positive integer and `MOHIST_PROVIDER_NON_RECOVERABLE_TERMS` as a JSON array of non-empty literal strings. The latter are escaped and matched case-insensitively in addition to built-ins; operator regex is never evaluated. Invalid configuration leaves Pi not-ready with an actionable diagnostic. `RunnerHost` builds one policy object and passes it to both OpenCode and Pi so defaults and overrides have one authority. Pi-native terminal errors are read from the final assistant message and normalized to `turn-failed`. The physical binding remains intact for all provider and deadline failures.

**Rationale:** sharing policy prevents OpenCode and Pi semantics from drifting while leaving protocol and lifecycle complexity inside each deep module. **Alternatives considered:** duplicating pattern and deadline logic was rejected because one product rule would gain two authorities; extracting a common `AgentRuntime` was rejected because it would expose a lowest-common-denominator lifecycle API.

### D8. Project Pi events through a durable Workflow Session outbox

`runtime/pi/event-projection.ts` converts smoke-verified Pi events into existing Mohist event types:
- message text/thinking -> `message.delta` / `reasoning.delta`;
- tool lifecycle -> `tool_call.started|updated|completed` keyed by `toolCallId`;
- assistant usage snapshots -> non-negative `usage.updated` deltas keyed by message ID;
- model observations -> `model.resolved`;
- unknown events -> runtime diagnostics only.

The projector keeps per-message text/usage state and per-tool fingerprints so duplicate SDK callbacks produce no second delta. At prompt completion it reconciles the final Session messages through the same projector, filling missed final text/usage without making events a completion authority.

Add a `WorkflowSessionEventOutbox` abstraction with a production file-backed adapter under `<runnerRoot>/.mohist/runner-state/` and an in-memory fake for tests. One stream is identified by logical AgentSession plus physical binding; each record has a monotonically increasing sequence and the normalized event payload. Appending a record is durable before delivery is attempted. Startup reloads pending streams and the host drains them in order independently of work-result reporting.

The Server runtime-event command carries stream identity and sequence. AgentSession state stores the last applied sequence for the current binding. Under the same aggregate transition that applies transcript/usage/model facts, it ignores and acknowledges `sequence <= lastApplied`, accepts only `lastApplied + 1`, and rejects a gap or stale binding before state mutation. This makes an ambiguous POST safe to retry without double-counting usage or text.

After D5 binding succeeds, `session.input` is appended and must receive a Server acknowledgement before prompt admission. After admission, projected events are appended to the durable outbox in order; the Action waits until its projector queue is durably appended, then makes a bounded drain attempt before returning. Delivery failure does not flip the TaskRun or replay the Prompt: the durable record remains and background/startup drain retries it. Before the next turn or a runtime rebind on the same logical Session, the old stream must drain completely. Thus transport failure can delay Session visibility but cannot permanently discard required audit facts.

**Rationale:** a source-specific durable outbox reuses the canonical Session API without importing AgentJob ownership or completion semantics, and cursor dedup makes ambiguous delivery safe. **Alternatives considered:** reusing `AgentJobExecutor`'s observer was rejected because it carries AgentJob IDs and output ownership; logging and discarding failed events was rejected because it violates Session audit requirements; failing and retrying the TaskRun after any event POST failure was rejected because prompt admission is not idempotent.

### D9. Update packaging and narrow Web classification only

Update root and Runner Node engine constraints, `CONTRIBUTING.md`, the Runner dependency/lockfile, CI's Node setup to an explicit >=22.19 release, and the Docker builder's Node installation so root `npm ci` satisfies the engine even though that image builds only Web. Do not rely on a distro's unspecified Node 22 minor.

The Web change is limited to treating `mohist/pi` as an Inline Agent task for Session/task-log milestones. Transcript, lineage, runtime, tool, and usage DTOs are already runtime-neutral. No Pi model selector or credential UI is added.

**Rationale:** engine enforcement must be consistent anywhere the monorepo lockfile is installed, while product UI work stays within the issue's Session visibility acceptance criterion. **Alternatives considered:** suppressing npm engine checks in Docker was rejected because it masks an unsupported toolchain; adding Pi-specific Session components was rejected because the existing projections already model runtime-neutral facts.

### D10. Verify each boundary with deterministic fakes

The Pi module exposes a factory seam that can construct fake model services, Session managers, AgentSessions, and event streams. Runner tests use fake timers and virtual paths; they do not touch Pi's real Session store. Coverage is split by responsibility:
- Pi runtime unit/spec tests: readiness including empty catalog, trust/resource setup, create/open/cache, literal prompt, model/thinking changes, event dedup/reconciliation, provider policy, warning/deadline/abort confirmation, missing file, and no replay.
- Pi Action specs: recursive input expansion, no hidden `vars.agent`, unknown-key diagnostics, binding-before-prompt, error mapping, private final text, promise projection, and cleanup reuse.
- Server specs/unit tests: runtime-aware Workflow open/bind, existing OpenCode caller migration, expected-binding races, symmetric cross-runtime lineage, workspace mismatch, Pi registration, stream sequence duplicate/gap/stale rejection, transcript/tool/usage projection, classification, and parent context.
- Outbox tests: durable append before send, input acknowledgement before Prompt, ambiguous acknowledgement retry, ordered gap recovery, Runner restart drain, pre-rebind drain, and bounded post-admission delivery failure without Prompt replay.
- Web tests: Pi inline-Agent classification and milestone rendering.

Run Runner typecheck/tests, Server tests, and Web typecheck/tests required by the repository. The one real SDK smoke is an explicit implementation gate and is not part of default test suites.

**Rationale:** tests remain deterministic and fast while the smoke catches real upstream drift. **Alternatives considered:** running Pi in default integration tests was rejected because it requires provider/network/filesystem state and would violate the repository's hard test boundaries.

## Risks / Trade-offs

- [Pi SDK API or event drift invalidates the call map] -> Pin an exact version, run and record the D2 smoke first, and confine all SDK access to `runtime/pi`.
- [In-process Pi failure can terminate the Runner and all active turns] -> Accept the issue's in-process topology; persist bindings before prompts, never auto-replay uncertain turns, and rely on Runner restart plus lazy Session restore for later work.
- [Pi readiness blocks unrelated OpenCode and AgentJob claims] -> Treat an empty credentialed catalog as ready, retry failed initialization with an actionable diagnostic, and keep the combined gate explicit until a future scheduler supports capability-aware claiming.
- [Runner crash before Pi writes the first assistant message leaves a bound path with no file] -> Preserve the binding and fail later restore with `runtime-session-missing`; require explicit Reset rather than fabricating continuity.
- [Two first turns race to create different physical Sessions] -> Serialize by logical Workflow Session before open/create/bind and also guard the bind with expected-current state in the AgentSession grain.
- [An unconfirmed abort leaves the old Pi turn running] -> Quarantine that physical path, reject later admission on it, and clear quarantine only on observed stop or Runner process restart.
- [Binding succeeds but the Runner crashes before prompt admission] -> The next turn safely restores an empty or existing Session; no prompt replay is needed.
- [Event delivery fails after a prompt has executed] -> Durably append ordered facts locally, retry through a Server cursor that deduplicates ambiguous delivery, drain on restart/before the next same-Session turn, and never retry the Prompt.
- [Duplicate SDK callbacks inflate transcript or usage] -> Deduplicate in the Pi projector by smoke-verified message/tool identity and emit usage deltas from snapshots.
- [Absolute session-file paths expose host layout in existing Session metadata] -> Keep the path in the already-authorized runtime binding field and diagnostics only; do not place it in Action output or user-authored Workflow variables.
- [Provider exhaustion wording is not recognized] -> Share the configurable pattern set and fall back to the bounded consecutive-retry threshold; retain original provider text in diagnostics.
- [Node 22.19 floor breaks CI or image builds using an older Node 22 minor] -> Pin CI and Docker builder Node versions before adding the dependency; fail installation early rather than shipping an unsupported runtime.
- [Required Workflow bind fields break the existing OpenCode caller] -> Migrate the shared Server wire and `mohist/opencode` caller atomically and pin both runtime-switch directions with regression tests.
- [Rollback leaves persisted Pi bindings unreadable by an older Runner] -> Keep stored Session rows queryable; older code will treat runtime `pi` as unavailable and require restoring the new Runner before execution resumes.

## Migration Plan

1. Pin the Pi SDK and Node >=22.19, update `CONTRIBUTING.md` plus the lockfile/CI/Docker toolchains, run the real SDK smoke, and commit `sdk-smoke-verification.json`. Stop and reconcile the runtime design if the smoke differs.
2. Implement `runtime/pi` behind fake SDK services, including readiness, untrusted resource loading, Session cache/restore, event projection, deadlines, provider policy, and error normalization.
3. Add the runtime-aware Workflow Session open/bind contract and event stream cursor in the Server; migrate `mohist/opencode` atomically; land expected-binding, symmetric lineage, duplicate/gap, and OpenCode regression tests.
4. Add the durable Workflow Session event outbox and host startup/background drain with fake-store tests.
5. Update `design/workflow/task-dispatch.md`, then update Server classification/validation/parent-context selection to the explicit inline-Agent Action set.
6. Add `piRuntime` to Runner host/executor context, combined readiness gating, the keyed logical Session queue/quarantine, outbox-backed reporter, `mohist/pi` Action, promise projection, and cleanup reuse. Leave AgentJob and Session handlers unchanged.
7. Update the narrow Web inline-Agent classifier.
8. Run the required Runner, Server, and Web checks. Add one fake-backed Workflow spec that exercises open -> bind -> durable input/events -> final text -> promise output end to end without real external dependencies.
9. Deploy Runner and Server together because the new Action sends runtime-aware Workflow Session requests. Existing OpenCode bindings remain valid; the Server state schema gains a current-binding event cursor, and Pi bindings/outbox streams are created on first Pi use.
10. Rollback by reverting the change and redeploying the previous Runner/Server pair after draining the event outbox. Existing OpenCode workflows continue normally. Workflow tasks already authored with `mohist/pi` will fail as unknown and persisted Pi Sessions remain audit-only until the Pi-capable version is restored; no automatic downgrade or binding rewrite is attempted.

## Open Questions

- Which exact `@earendil-works/pi-coding-agent` version is pinned? Resolve in migration step 1 from the latest version whose real smoke passes the required surface.
- What exact SDK option enforces project untrusted mode in the pinned version: a `projectTrusted: false` service option or explicit project-resource overrides on `SettingsManager`/`DefaultResourceLoader`? The smoke must prove equivalent behavior before implementation.
- Which fields in the pinned event payload are stable identities for assistant messages, text updates, tool calls, and usage snapshots? Record them in the smoke artifact and use them as the projector's dedup keys.
- Does `SessionManager.create(cwd)` expose an absolute `sessionFile` before the file is materialized, and which state reliably confirms `abort()` has stopped streaming? These are implementation gates, not product decisions; if the answers differ from the current runtime design, update that design before proceeding.

There are no open product-scope decisions. Session commands, AgentJob routing, and model-catalog UI remain separate issues.
