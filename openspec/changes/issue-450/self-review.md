# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Runner/Server persistence and dispatch behavior, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: Action-event acknowledgement is not specified as a durable commit

The plan requires `session.input` acknowledgement before Prompt admission and says the Action-event cursor advances atomically with transcript/usage/model facts (`design.md:164-166`; `tasks.json:73-74,103`). The current `AgentSessionGrain.AppendRuntimeEventsAsync` path only mutates activation state and an in-memory transcript accumulator, schedules a persistence timer, fans out, and returns (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:742-856`). State/domain events and transcript rows are then persisted in separate phases (`AgentSessionGrain.cs:1158-1222`).

T-003 assigns a new route and cursor transition but never requires acknowledgement to wait for durable state, nor defines durable pending transcript evidence when transcript persistence follows cursor persistence. A crash after HTTP acknowledgement can therefore lose `session.input`; a cursor commit followed by transcript failure can also make retry look duplicate while the audit fact is absent. Define and assign the durable Action-event commit protocol: persist cursor, state/domain facts, and idempotent pending transcript evidence before acknowledgement, then project/retry transcript separately (or use one transaction). Add crash-after-ack, transcript-failure-after-cursor, duplicate retry, and reactivation tests.

### F-2 High: Workflow lease ownership is split between T-003 and T-006

D6 says `WorkExecutor` acquires the complete task lease and passes an already-held lease token to the Action; the Action cannot acquire or release the coordinator (`design.md:121`). T-003 nevertheless owns migrating the production OpenCode Action into the coordinator (`tasks.json:64,67`), while T-006 separately owns `WorkExecutor` integration and says Action contexts carry `WorkflowSessionTurnCoordinator` itself (`tasks.json:153,156`). These contracts permit two incompatible implementations: Action-owned acquisition in T-003 or executor-owned acquisition in T-006.

Assign production lease acquisition and the OpenCode migration to one task, and make the Action-facing type explicit. If D6 remains authoritative, T-003 should implement/test the coordinator as an injected boundary only, while T-006 atomically migrates `WorkExecutor`, OpenCode, and Pi so Actions receive only the held lease token needed for open/rebind/quarantine operations.

### F-3 High: The universal pre-dispatch Pi command guard is not race-safe for Cancel

The revised plan promises every Pi Follow-up/Compact/Reset/Cancel rejects before reservation or Runner dispatch (`proposal.md:25`; `design.md:98,125`; `specs/pi-workflow-session/spec.md:93,134-137`; `tasks.json:76,161`). Reset can enforce this inside the grain before creating `PendingReset`, but Cancel currently resolves a binding snapshot, separately checks the grain, and later dispatches that stale target (`packages/server/src/Mohist.Server/Api/AgentSessionCancelRoutes.cs:69-129`). A Workflow rebind can commit between the check and dispatch; the Runner Cancel handler then invokes OpenCode for the stale wire binding.

Because the plan explicitly excludes command admission fences and coordination, its universal guarantee cannot be implemented for concurrent Cancel. Either add a narrow durable Cancel admission fence respected by Workflow bind, or narrow the contract to reject commands whose linearized grain admission observes a Pi binding and explicitly permit an already-admitted OpenCode Cancel to finish against its old physical target. Add controlled cancel-check/rebind/dispatch race tests. T-003 must also explicitly remove Reset's current unregistered-runtime OpenCode fallback before creating its reservation (`AgentSessionGrain.cs:284-304`).

### F-4 Medium: Crash redelivery's accepted duplicate-turn limitation is omitted

The canonical Pi design explicitly accepts that Workflow redelivery in the Runner crash window can duplicate a turn (`design/runtimes/pi.md:195-199`). The change artifacts repeatedly say restart repair and "no replay" without distinguishing Runtime/outbox retries from Server redelivery (`proposal.md:9-11`; `design.md:162,168`; `tasks.json:106,109,112,170`). Current Runner ownership is process-local, and the Server redelivers Running work absent from the restarted Runner's report (`packages/runner/src/runtime/host.ts:342-399,455-459`; `packages/server/src/Mohist.Server/Runner/Services/DispatchService.cs:98-114`). The repaired Action checkpoint does not own TaskRun completion, so redelivery can submit the prompt again.

Do not invent durable TaskRun recovery in this issue. Instead, state the canonical accepted limitation explicitly and scope every no-replay assertion to Runtime/outbox behavior within one delivered execution. Add a restart/redelivery test that documents the possible duplicate turn while proving the Runtime and outbox themselves never resubmit an uncertain Prompt.

## Structural Checks

- `tasks.json` parses as valid JSON; all seven task IDs and dependencies resolve and the graph is acyclic.
- All referenced spec files and requirement anchors resolve.
- All three proposal capabilities and the issue's seven acceptance criteria are represented.
- The previous Session-command implementation, Runtime slot callback, Compact reconciliation, and Follow-up terminal-observer findings are removed from the plan.
- Catalog loading is used only for Runtime readiness; catalog reporting and Web selection remain out of scope.
- Pi AgentJob execution, Pi Session-command implementation, ACP/RPC, and a generic `AgentRuntime` remain outside implementation scope.

## Verdict

The product surface is covered, but builders would still have to choose persistence, lease-ownership, and Cancel linearization protocols, and the restart contract currently overstates the canonical no-replay guarantee.

<promise>FAIL</promise>
