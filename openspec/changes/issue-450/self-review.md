# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Runner Runtime/command wiring, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: The `prompt-active` transition has no Runtime reservation handshake

The coordinator must remain `preparing` until the selected Runtime synchronously reserves the physical Session slot, then enter `prompt-active` without a gap before SDK Prompt admission; this distinction is what prevents a Follow-up from starting an idle Prompt during Workflow preparation (`design.md:123-127`; `specs/pi-workflow-session/spec.md:172-190`). T-003 exposes/tests the phases but no task owns a callback/token API between the already-held Workflow lease and either Runtime (`tasks.json:66,84`).

Today `OpenCodeRuntime.runTurn()` acquires its private in-flight slot inside the opaque async call, after the Action invokes it (`packages/runner/src/runtime/opencode/runtime.ts:214-239`; `packages/runner/src/actions/opencode.ts:157-158`). The Action cannot know when reservation succeeds: marking `prompt-active` before `runTurn()` recreates the idle Follow-up race, while marking it after `runTurn()` resolves is too late. Define a Mohist-owned reservation handshake for OpenCode and Pi that synchronously reserves the physical slot, transitions the existing coordinator lease, admits the SDK Prompt, and restores `preparing` before slot release. Assign OpenCode boundary work to T-003, Pi boundary work to T-002, T-006 composition, and no-gap/failure-release tests.

### F-2 High: T-006 assumes production Compact/Reset execution that is not wired or implemented

T-006 says it will wire existing Workflow-origin OpenCode Compact/Reset handlers to command leases, outbox drains, and the completion callback (`tasks.json:156,165`). The repository has a generic `registerSessionCommandHandler` and durable `SessionCommandJournal`, but `RunnerSignalR.registerHandlers()` does not register that handler (`packages/runner/src/server/runner-signalr.ts:139-166`), and `OpenCodeRuntime` exposes turn, Follow-up, and Cancel behavior but no Compact/Reset operations. Calling these paths "existing handlers" hides the actual implementation surface.

The plan also does not assign started-journal restart reconciliation after Runtime side effects. A Runner crash after Compact/Reset changes OpenCode but before receipt persistence must reconcile the journal entry against Server reservation/receipt state without repeating the Runtime effect or leaving Workflow admission permanently blocked. Explicitly assign OpenCode compact/reset Runtime boundary methods, SignalR registration, target validation, journal construction/loading, `reconcileStarted`, idempotent completion callback, shutdown/restart behavior, and deterministic crash-window tests. If that compatibility migration is intentionally too large for this issue, simplify the Action-stream design so existing Session commands do not require unimplemented production execution.

### F-3 High: Idle Follow-up leases have no actual terminal-evidence source

The plan requires an idle OpenCode Follow-up command lease to remain held until `session.closed` or `session.followup_failed` (`design.md:129`; `specs/pi-workflow-session/spec.md:95`; `tasks.json:68`). The current Follow-up path calls `OpenCodeRuntime.followup()`, which awaits only `promptAsync` acceptance, and immediately records `session.followup_completed` when that promise resolves (`packages/runner/src/runtime/opencode/runtime.ts:245-257,309-321`; `packages/runner/src/server/followup-handler.ts:128-150`). That is admission evidence, not turn termination.

No task defines who subscribes to actual OpenCode terminal events, how physical Session events correlate to the Follow-up operation/coordinator lease, how failure-outbox delivery releases the correct lease exactly once, or how Runner restart settles the persisted Follow-up reservation. Define the terminal observer/reconciliation protocol and assign its production wiring and tests. The tests must cover accepted-but-running Follow-up, normal terminal event, terminal failure, duplicate/out-of-order event, disconnect/reconnect snapshot reconciliation, and restart without early release or permanent Workflow blockage.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic, priorities are ordered, and every implementation task reaches T-001.
- All referenced spec files and requirement anchors resolve.
- All three proposal capabilities and the issue's seven acceptance criteria are represented.
- The prior command-receipt, ownership, cache-write API projection, and Pi command non-routing findings are now explicitly resolved.
- Pi AgentJob execution, Pi Session-command routing, catalog/UI selection, ACP/RPC, and a generic `AgentRuntime` remain outside implementation scope.

## Verdict

The product behavior is covered, but the plan still relies on three execution seams that do not exist in the current Runner and are not assigned for implementation. Builders would have to invent concurrency-critical Runtime and Session-command protocols.

<promise>FAIL</promise>
