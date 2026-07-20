# Self-Review - Issue #450 Pi Workflow Path

Scope: current issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts and repository architecture/testing rules. This review modifies no plan artifact.

## Findings

### F-1 Critical: The durable Session event protocol has no implementation task

The revised Session spec requires a durable Runner outbox, stable stream identity and monotonic sequence, an idempotent Server cursor, duplicate acknowledgement, gap recovery, startup/restart drain, and a pre-rebind drain barrier (`specs/pi-workflow-session/spec.md:110-147`). D8 designs those components explicitly (`design.md:134-151`), and migration steps 3-4 separate the Server cursor from the Runner outbox (`design.md:195-196`).

`tasks.json` still represents the earlier lossy reporter. T-003 owns only stale-binding event rejection (`tasks.json:66-68`), while T-004 asks the reporter to flush and log post-admission delivery failures (`tasks.json:90`) but never owns durable append-before-send, the file-backed store, sequence/cursor persistence, duplicate/gap behavior, background/startup drain, or restart recovery. Its end-to-end coverage likewise mentions only stale-event rejection (`tasks.json:95`). The required audit guarantee can therefore be omitted while every task criterion passes. The task graph must assign the Server cursor and Runner outbox as explicit, dependency-ordered work with deterministic fake-store tests.

### F-2 Critical: T-003 breaks the existing OpenCode Workflow caller instead of migrating it atomically

T-003 makes Workflow open require `runtime` and bind require expected-current fields (`tasks.json:60-63`) but never updates or regression-tests the existing `mohist/opencode` caller. That caller currently sends neither field (`packages/runner/src/actions/opencode.ts:96-108,135-145`). D5 explicitly requires the Server wire and OpenCode caller to migrate atomically and requires both runtime-switch directions to be covered (`design.md:86-95,166,188`).

As written, T-003 is not independently deliverable despite its note (`tasks.json:76`): it can break the only working Workflow Inline Agent path before T-004 starts. Its one directional "different runtime to pi" criterion (`tasks.json:62`) also omits the normative Pi-to-another-runtime case (`specs/pi-workflow-session/spec.md:61-65`). T-003 must include the OpenCode caller migration and symmetric regression tests in the same task as the required wire change.

### F-3 High: A Pi-owned queue cannot enforce logical Session serialization across runtimes

D5 permits a logical Workflow AgentSession to switch between OpenCode and Pi in either direction (`design.md:88-95`), but D6 places the keyed queue in the Pi Action adapter/coordinator (`design.md:112-118`). An OpenCode Action never enters that queue. Concurrent OpenCode and Pi tasks using the same logical Session can therefore overlap: the expected-current bind guard can reject a stale bind, but it does not reserve the logical Session for the duration of the already-running physical Prompt.

This violates the runtime-neutral invariant that each logical AgentSession admits at most one work Prompt (`design/runtimes/opencode.md:171-173`) and the new Session requirement (`specs/pi-workflow-session/spec.md:89-108`). The serialization boundary must be shared by both Workflow Inline Agent callers, or cross-runtime rebinding must be removed from this issue. Tests must cover concurrent OpenCode/Pi turns against one logical Session, not only concurrent Pi turns.

### F-4 High: Post-admission durable-append failure has no defined safe outcome

D8 says every projected fact is durably appended before the Action returns and that delivery failure preserves the Action result because the durable record remains (`design.md:145-151`). It does not define what happens when the append itself fails after Prompt admission due to storage exhaustion, permission loss, corruption, or an interrupted/partial write. At that point failing the task can invite an unsafe Workflow retry, while succeeding permanently violates the required audit record (`specs/pi-workflow-session/spec.md:110-129`).

The design must specify the crash-consistent record/ack format, the Action result and physical-Session admission state after an unpersisted post-admission fact, and recovery when a stream cannot be drained. Because repository tests may not instantiate physical filesystem adapters (`design/testing.md:45-55`), the production file-backed implementation also needs an injected storage/filesystem boundary so serialization, partial-write recovery, restart loading, and acknowledgement persistence can be tested in memory. The current plan mentions only an in-memory outbox fake (`design.md:145,163-167`), which bypasses the production durability logic.

### F-5 High: The governing working-directory contract is contradictory

The issue's explicit domain decision and the delta plan reject a different working directory (`proposal.md:10`; `specs/pi-workflow-session/spec.md:67-87`). The issue-designated product contract instead says a working-directory change creates a new physical Session and appends lineage (`docs/actions/pi.md:78-85`). The canonical runtime design says both that directory change creates a new physical Session (`design/runtimes/pi.md:120-123`) and that mismatch is rejected (`design/runtimes/pi.md:140-146`); the shared AgentSession design also permits replacement on work-directory change (`design/agent-execution.md:125-126`).

An implementer cannot treat all of these as authoritative. The canonical product/runtime/domain documents must be reconciled to the issue's selected rule before build work begins. T-005 only updates implementation-gap footnotes (`tasks.json:113-126`), so no task currently owns that contract correction.

### F-6 High: `tasks.json` was not reconciled with the hardened runtime and Action specs

Several newly normative behaviors have no matching task acceptance or test obligation. T-002 tests only an unconfirmed-stop diagnostic (`tasks.json:40`) and does not require quarantining the physical path, rejecting later admission, preserving other Sessions, or clearing quarantine on observed stop/restart (`specs/pi-runtime/spec.md:93-126`). It mentions provider policy mechanics (`tasks.json:31,41`) but does not own parsing and validating `MOHIST_PROVIDER_RETRY_THRESHOLD` and `MOHIST_PROVIDER_NON_RECOVERABLE_TERMS`, readiness failure on invalid input, literal matching, or one shared OpenCode/Pi policy object (`specs/pi-runtime/spec.md:150-168`; `design.md:130`).

T-004's input criterion (`tasks.json:86`) does not require the exact `session` identity matrix, recursive structured expansion, `options` object/null/type validation, or no-side-effect rejection before Session creation (`specs/pi-workflow-action/spec.md:17-59`). Its readiness criterion gates polling (`tasks.json:84`) rather than requiring both runtimes ready before Server registration as the spec and D3 state (`specs/pi-runtime/spec.md:17-37`; `design.md:54-62`). These omissions are material because autonomous builders use task acceptance to decide completion.

### F-7 Medium: Required design/toolchain migrations are absent from the task graph

D4 requires updating the architecture-owned parent-context rule in `design/workflow/task-dispatch.md` before changing dispatch code (`design.md:82,197`), but T-004 changes parent-context behavior without owning or depending on that spec update (`tasks.json:82,94`). T-001 also omits `CONTRIBUTING.md` even though the proposal and D9 explicitly include it in the breaking Node 22.19 migration (`proposal.md:13,28`; `design.md:153-159,193`); the contributor guide still advertises Node >=22.0.0 (`CONTRIBUTING.md:5-10`).

Both migrations need explicit task ownership and ordering. Otherwise implementation can satisfy the task graph while leaving authoritative architecture and contributor prerequisites stale.

### F-8 Medium: Cancellation parity is not specified with the same rigor as timeout

The issue requires cancellation semantics to remain equivalent to OpenCode. The design says only that an external Action signal follows interruption cleanup and maps to `interrupted` (`design.md:128`), while the runtime spec's detailed scenarios cover deadline interruption but not cancellation result fixation, abort confirmation, unconfirmed-stop quarantine, or no replay after cancellation (`specs/pi-runtime/spec.md:93-126`). T-002 mentions external interruption in one broad test criterion (`tasks.json:40`) and T-004 checks only the final error mapping (`tasks.json:92`).

Add a normative cancellation scenario and deterministic acceptance coverage for confirmed and unconfirmed abort outcomes. Without it, implementations can diverge on whether a cancelled task releases the Session while Pi may still be running.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All five task IDs and dependencies resolve; the graph is acyclic and every dependency points to a lower priority.
- All three proposal capabilities have matching spec files; the 21 requirements use correctly headed scenarios.
- Scope exclusions for Pi AgentJob routing, Session commands, ACP/RPC, and runtime-aware model-catalog UI are otherwise consistently stated.
- The worktree was clean before this review; only `self-review.md` is changed by it.

## Verdict

The revised proposal, design, and specs close the earlier lossy-delivery, quarantine, input-validation, provider-configuration, and runtime-switch contract gaps, but the executable task graph still reflects the older plan. In addition, cross-runtime serialization, durable local append failure, and the contradictory canonical working-directory rule remain unresolved design blockers. The plan is not ready for autonomous build execution.

<promise>FAIL</promise>
