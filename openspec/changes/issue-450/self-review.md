# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Session and Runner boundaries, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: Workflow admission and runtime rebind can race existing Session commands

The plan serializes only Workflow Inline Agent tasks in a Runner-local coordinator (`design.md:115-123`; `tasks.json:65-76`). It explicitly leaves the existing Follow-up/generic command routes outside the Action stream and says the guarded bind does not create a Session-command reservation (`design.md:90,98`). In the current Server, `BeginFollowupAsync` persists a lease against the current physical binding, while Compact/Reset persist a separate reservation; neither the planned open nor bind transition is required to reject those reservations, and Session commands do not enter `WorkflowSessionTurnCoordinator` (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:245-304,372-407`).

Consequently, an idle Follow-up can reserve and start a Prompt on the old binding after a Workflow Action opens the logical Session but before that Action rebinds or submits its own Prompt. The guarded bind can still succeed because the expected physical binding has not changed, allowing the old command turn and the new Workflow turn to overlap or letting the rebind detach an active command. This violates the shared AgentSession single-work-Prompt invariant and the claim that Session-command users are unaffected, even though implementing Pi command routing remains out of scope. Define one AgentSession-authoritative admission/fence interaction for Workflow work versus pending/active Follow-up, Compact, and Reset, and assign both race directions to Server/Runner tests.

### F-2 High: The sequenced Action stream has no defined bootstrap lifecycle

D5's bind command establishes runtime, physical ID, work directory, model, Runner, expected-current fields, and a drained sequence, but no Action stream identity (`design.md:92-98`). D8 creates the Runner manifest only after binding, while the Server is expected to store a stream identity/cursor for the current binding, accept only `lastApplied + 1`, and atomically seal that stream during rebind (`design.md:154-158`). Existing OpenCode bindings have neither a manifest nor a persisted Action cursor. T-003 owns the Server route/cursor/seal before T-004 owns manifest creation and OpenCode outbox migration (`tasks.json:67-75,93-106`).

The plan never states the canonical stream identity derivation, initial cursor value, transition that authorizes the first identity, or bootstrap behavior for an existing OpenCode binding with zero Action events. Builders therefore must invent whether bind derives/records the stream, the first event claims it, or the Server recomputes it; those choices produce different stale-event and zero-event-rebind behavior. Specify the stream identity and cursor lifecycle from first bind through legacy OpenCode bootstrap, first event, empty-stream drain, seal, and replacement, then assign the protocol to one task with persistence/reactivation tests.

### F-3 High: Logical quarantine can be entered but has no owned release path

The Session spec requires later work to become admissible after stop is observed or the Runner restarts (`specs/pi-workflow-session/spec.md:89-120`), and the design says a later stop observation clears both physical and coordinator quarantines (`design.md:121`). T-002 requires the runtime to clear only its physical quarantine and emit the signal used to quarantine the logical key (`tasks.json:43-45`). T-006 requires every unconfirmed interruption to quarantine the logical key before release, but does not require wiring or testing removal of that key (`tasks.json:158-162`).

A conforming build of the listed criteria can permanently reject a logical Session after the physical Pi turn has stopped. Assign the runtime-to-coordinator stop notification, logical-key cleanup on observed stop, process-restart initialization semantics, and tests proving admission resumes without replay.

### F-4 Medium: Outbox and host ownership overlap across tasks

T-004's description owns host startup/background drain and its acceptance criteria migrate existing OpenCode bindings to the outbox (`tasks.json:93-107`). T-006 again owns RunnerHost initialization, periodic storage startup retry, global outbox readiness gating, and host-level readiness tests (`tasks.json:147-164`). T-003 also requires the task lease to span final reporter persistence and tests drained-stream rebind before the reporter/outbox exists (`tasks.json:65-76`).

The dependency graph is acyclic, but these completion contracts overlap the same host, Action, and lifecycle seams, so T-003/T-004 cannot have a stable definition of done without implementing pieces assigned to later tasks or leaving temporary integration that T-006 replaces. Repartition ownership so T-003 delivers coordinator and Server protocol against explicit fakes, T-004 owns the complete outbox plus its host lifecycle (or only the standalone outbox), and T-006 owns integration exactly once.

### F-5 Medium: Omitted model/variant preservation lacks task acceptance

The runtime and Action specs require omitted selections to preserve the current Session choice, or use Pi defaults for a new Session (`specs/pi-runtime/spec.md:83-97`; `specs/pi-workflow-action/spec.md:27-31`). T-002 tests applying supplied selections, and T-006 tests input shape, hidden-variable rejection, and model changes without binding rotation, but neither requires omitted values to avoid resetting an existing Session selection (`tasks.json:41,151-154`).

Add explicit runtime and Workflow reuse tests for omitted model, omitted variant, and both omitted on new versus restored Sessions. Otherwise an implementation can satisfy the listed task criteria while calling setters with absent values and losing the conversation's current selection.

### F-6 Medium: Global outbox failure does not own its required diagnostic

The Session spec requires outbox root/capability failure to expose a credential-redacted actionable storage diagnostic as well as block registration and claiming (`specs/pi-workflow-session/spec.md:178-182`). T-004 requires the readiness gate but not the diagnostic, while T-006's credential test covers Pi auth/provider values rather than a storage diagnostic (`tasks.json:104,149-163`). Add the diagnostic shape, masking assertion, and recovery assertion to the task that owns outbox readiness.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic and every implementation task reaches T-001.
- All task spec paths and requirement anchors resolve.
- All three proposal capabilities have matching spec files; their normative scenarios have task coverage except for the gaps above.
- The fixed 60-minute Workflow Prompt budget and distinct `cachedWriteTokens` persistence/API/Web projection reported by the previous review are now explicitly modeled and assigned.
- The issue's direct-Workflow boundary remains intact: Pi AgentJob routing, Pi Session-command routing, and runtime-aware catalog/UI work are not assigned for implementation here.

## Verdict

The issue behavior is represented, but builders still have to invent concurrency and stream-bootstrap semantics, and the task graph does not own logical quarantine release. These are correctness gaps at shared Session boundaries, not implementation details.

<promise>FAIL</promise>
