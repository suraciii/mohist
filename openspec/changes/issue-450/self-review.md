# Self-Review - Issue #450 Pi Workflow Path

Scope: issue #450 and `openspec/changes/issue-450/{proposal.md,design.md,tasks.json,specs/}`, checked against the issue-designated product/runtime contracts, current Runner Runtime/command wiring, repository architecture, and testing rules. This review modifies no other file.

## Findings

### F-1 High: The revised plan implements Session commands that issue #450 explicitly excludes

Issue #450 limits delivery to direct Workflow execution and names Session-command routing and implementation as sister-issue work. The proposal repeats that boundary (`proposal.md:14`), and the design says Session-command users are deliberately unaffected (`design.md:13,26-32`). Revised T-006 nevertheless adds production OpenCode `compact()` and `reset()` Runtime methods, registers `registerSessionCommandHandler`, implements command result routing, and adds command restart behavior (`tasks.json:161,168-171`). These are currently absent product capabilities, not compatibility changes to already-running handlers.

This substantially expands the high-risk issue with an independent command vertical slice and contradicts its non-goals. Remove the new Compact/Reset implementation dependency from #450 and simplify the Action-stream/coordinator plan so unavailable commands remain unavailable without blocking Pi Workflow delivery. If OpenCode command implementation is genuinely prerequisite, the issue/spec boundary must be changed explicitly rather than burying that product scope in T-006 acceptance criteria.

### F-2 High: The slot callback still cannot restore `preparing` before Runtime slot release

The design requires `prompt-active` to begin only after physical-slot reservation and to end before that slot is released (`design.md:127`). Revised T-002/T-003 add `onSlotReserved`, but T-003 says the Action calls `exitPromptActive` on `runTurn` completion (`tasks.json:42,68-69`). Today OpenCode owns `inFlight.end(sessionKey)` in `runTurn`'s internal `finally`, before the returned promise settles for the Action (`packages/runner/src/runtime/opencode/runtime.ts:214-242`). The proposed caller therefore cannot perform the required exit ordering. Cancellation and thrown-error wording does not close that gap.

Define one scoped reservation lifecycle whose Runtime-owned `finally` invokes a synchronous release callback before ending the physical slot, or return a single-use reservation handle whose release ordering is explicit. Specify the equivalent Pi ordering, callback-failure behavior, and tests that assert both edges: reserve -> enter -> Prompt admission and Prompt settlement -> exit -> physical release.

### F-3 High: Compact crash reconciliation assumes an effect can be proven when no correlation evidence exists

T-006 says `reconcileStarted` will inspect OpenCode and decide that compaction was applied or that no effect occurred, without repeating the effect (`tasks.json:170`). OpenCode Compact is `session.summarize` on the same physical Session (`design/runtimes/opencode.md:318-324`); the journal operation ID is not supplied to that SDK call, and the plan defines no durable before/after marker that attributes a summary to this operation. A post-restart snapshot can therefore show changed context without proving whether this command caused it, while an unchanged snapshot cannot prove the call never started.

The required `completed` versus `not-started` decision is not implementable from the stated evidence and risks either duplicate compaction or falsely abandoning an applied effect. Define a verified, durable correlation protocol before Runtime entry and include its SDK/storage evidence in the design and smoke/tests, or retain `indeterminate` and move this command implementation to its owning issue. Reset needs the same explicit evidence analysis rather than a generic "physical session reflects the side effect" assertion.

### F-4 High: The Follow-up observer names undefined events and still lacks reconnect correlation

Revised T-006 assigns an observer, but it says native `session.completed`/`session.error` events correlate to the operation and then produce Mohist `session.closed`/`session.followup_failed` evidence (`tasks.json:171`). Neither the current OpenCode Runtime nor its canonical event design defines those native terminal event names; the implemented subscription exposes untyped OpenCode envelopes and current turn logic recognizes `session.status` plus snapshot reconciliation (`packages/runner/src/runtime/opencode/event-subscription.ts:16-31`; `packages/runner/src/runtime/opencode/turn.ts:539-546`; `design/runtimes/opencode.md:245-260`). OpenCode events also carry Session/directory identity, not the Mohist Follow-up operation ID.

Consequently the plan still does not define how an idle Follow-up distinguishes its own terminal transition from stale/previous Session events, what snapshot proves completion after disconnect, or how that evidence maps exactly once to the persisted reservation. Specify the smoke-verified native terminal signal, an admission baseline/generation or other operation correlation, reconnect snapshot rules, and the exact producer of each Mohist terminal event before assigning implementation tests.

## Structural Checks

- `tasks.json` parses as valid JSON.
- All seven task IDs and dependencies resolve; the graph is acyclic, priorities are ordered, and every implementation task reaches T-001.
- All referenced spec files and requirement anchors resolve.
- All three proposal capabilities and the issue's seven acceptance criteria are represented.
- The revised `tasks.json` assigns the previously missing seams, but assignment alone does not resolve the scope and protocol contradictions above.
- Pi AgentJob execution, Pi Session-command routing, catalog/UI selection, ACP/RPC, and a generic `AgentRuntime` remain outside implementation scope.

## Verdict

The Pi product behavior is covered, but the revised task plan crosses the issue's explicit command boundary and still leaves reservation release, command reconciliation, and Follow-up terminal evidence under-specified. Builders would have to invent concurrency-critical protocols.

<promise>FAIL</promise>
