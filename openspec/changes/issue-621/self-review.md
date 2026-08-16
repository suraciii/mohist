# Issue 621 Plan Self-Review

Review round: first review. No previous `self-review.md` existed. The issue was read with `mo issue view 621 --project proj_f6c141d63b6243bfbb481737b2243b87 --json body` before reviewing the plan artifacts.

## Verdict

FAIL. The plan is not ready to build because it implements a different publication contract from issue 621 and adds a Server-side change that the issue explicitly excludes.

## Must-Fix Findings

### F-001: The plan violates the issue's Runner-only boundary

Evidence:

- Issue 621 Product Shape says the corresponding guard is added on the Runner side, the detection logic must not enter Server, and Server must never write a missing reply.
- Issue 621 Non-Goals explicitly exclude Server-side unpublished detection and say Server-side changes are out of scope.
- `design.md:44-56` chooses a Runner-authenticated ServerConnection publication probe backed by `SlackOutboxStore`.
- `tasks.json:6-24` makes that probe, its Server implementation, and Server tests the first task.

This is not merely an implementation detail. Leaving it in the plan guarantees a forbidden Server API/outbox-read change and makes the plan incomplete relative to the issue's scope. Remove the Server probe and Server matching/tests from the plan and define the detection entirely at the Runner turn/action-observation boundary.

Violates: issue 621 Product Shape and Non-Goals (no Server-side unpublished detection; Runner-only scope).

### F-002: Publication is detected by the wrong fact, so failed reply-action calls trigger an advisory

The issue defines the guard condition as the absence of a reply action call. It also explicitly says to recognize an attempted publication rather than publication success: a failed send already returns non-zero feedback to the model. Acceptance Criterion 3 requires a Turn with an existing reply action call to be unaffected.

The plan instead defines publication as successful Server outbox acceptance:

- `design.md:18-24` and `design.md:44-56` make outbox acceptance the authoritative predicate.
- `specs/runner-reply-guard/spec.md:1-28` and `tasks.json:32-38` suppress the advisory only for an accepted outbox row.

Failure case: the Agent invokes `mo slack message send`, the endpoint rejects it or no live Connection accepts it, and the turn then terminates. There is no accepted outbox row, so this plan issues the advisory even though the reply action was attempted. That directly violates the issue's acceptance criterion that an existing reply action call has no effect from the guard, and changes the intended feedback loop from "the Agent already tried" to "the Server later accepted it". A Server probe cannot repair this semantic mismatch; the plan must observe the Runner-local reply-action invocation/attempt and test rejected-send attempts explicitly.

Violates: issue 621 Product Shape constraint (recognize an attempted publication, not success) and Acceptance Criterion 3 (existing reply action call is unaffected).

### F-003: The plan hard-codes one advisory while the issue specifies a default budget of two

`proposal.md:7-11`, `design.md:20`, `design.md:66-72`, and `tasks.json:34-36` all specify at most one advisory per turn. Issue 621's Product Shape says the advisory may be issued a finite number of times with a default of two before the Turn ends normally. The plan therefore cannot deliver the stated default reminder budget; a model that needs the second bounded reminder is terminated after only one opportunity.

The plan must define the bounded reminder count in accordance with the issue (default two), make the count explicit in the coordinator state, and verify that the configured/default count cannot loop. This is separate from the requirement that the reminder text license silence.

Violates: issue 621 Product Shape constraint (finite reminders, default two) and the related bounded-reminder goal.

### F-004: T-003 evaluates Pi follow-ups at admission, not at the Turn's terminal point

The proposed follow-up integration says to run the guard after the original follow-up result and observer facts but before `recordFollowupActivity` (`tasks.json:52-73`; `design.md:74-80`). That result is not always a terminal result in the current Pi implementation:

- `packages/runner/src/runtime/pi/runtime.ts:454-458` handles an already-streaming Session by calling `session.steer(request.prompt)` and returning a successful follow-up result immediately.
- `packages/runner/src/server/followup-handler.ts:256-317` attaches the completion callback to that result and then records terminal activity.

Consequently, a Slack Pi follow-up received while the Session is streaming can cause the proposed guard to run while the original model turn is still active. The advisory may be injected before the Turn has ended, and the existing activity closeout may be emitted before the actual model stream reaches its terminal state. That fails the issue's requirement that the reminder be issued when a Slack-source Turn ends, and can also violate the bounded single-advisory behavior through premature/duplicate terminal handling.

T-003 must specify and test the actual terminal boundary for both Pi follow-up branches, including the streaming/steer branch. It cannot use follow-up admission as the terminal signal.

Violates: issue 621 Acceptance Criterion 1 (reminder when the Slack Turn ends) and Acceptance Criterion 2 (no reminder loop/early duplicate behavior).

## Dimension Review

### Issue goals and acceptance criteria: FAIL

The issue was re-read before the artifacts. The plan covers Slack eligibility, a reminder, bounded processing, and non-Slack bypass at a high level, but it fails the issue's Runner-only scope, uses accepted delivery instead of attempted reply-action calls, omits the stated default reminder count of two, and does not establish a true Pi follow-up terminal boundary.

### Coverage: FAIL

The plan has scenarios for accepted outbox rows, explicit silence, one-shot state, and both runtime families, but it has no scenario for "reply action invoked and rejected" even though that is the issue's defining distinction. It also has no scenario for two bounded reminders or for a streaming Pi follow-up whose `steer` result precedes turn termination. T-001 covers behavior explicitly outside the issue instead of covering the missing Runner-local attempt signal.

### Correctness: FAIL

The concrete failure cases above produce the wrong behavior: a failed-but-attempted reply receives an advisory, the plan introduces an excluded Server dependency, and a streaming Pi follow-up can be guarded before completion. The proposal/spec agreement is internal to the plan but does not make that behavior correct against issue 621.

### Current codebase and conventions: FAIL

The valid Slack context and existing Pi/OpenCode observer surfaces are compatible with a Runner-local guard. However, the plan crosses the explicit Runner/Server ownership boundary and assumes the result of `PiRuntime.followup` is a terminal result in a branch where the current runtime contract says it is only prompt admission. Those are codebase-inconsistent assumptions, not cosmetic differences.

### Task breakdown, ordering, and verifiability: FAIL

The task graph itself is structurally sound: `tasks.json` parses, all three dependencies name existing tasks, and the graph is acyclic (`T-001 -> T-002 -> T-003`). The breakdown is nevertheless incomplete relative to the issue: T-001 implements the forbidden Server probe, no task owns local reply-action-attempt detection or rejected-send tests, no task implements the default-two reminder budget, and T-003 does not define how actual Pi terminal completion is observed. The listed tests would verify the plan's alternate contract rather than the issue's acceptance criteria.

## Observations

- `design.md:74-80` deliberately delays follow-up `session.activity` closeout until guard processing completes. This preserves the eventual status and payload but can leave the session visibly working for the advisory timeout; the issue's liveness non-goal says terminal closeout should continue as usual. The timing impact should be resolved when the must-fix lifecycle boundary is redesigned, but it is not a separate verdict-driving finding here.
- `design.md:82-86` scopes guard state to the active Runner operation. A Runner restart before any reply attempt is accepted would lose the one-shot state, so a later duplicate/reconciliation signal could issue another advisory. The issue does not explicitly require cross-restart persistence, but the implementation should document whether the at-most-bounded reminder guarantee is process-scoped or turn-scoped and test the chosen boundary.
- `design.md:104-108` leaves the timeout and diagnostic naming open. A finite code-level timeout is sufficient for the issue, so these are implementation decisions rather than must-fix plan defects.

No product files were modified. Only this review artifact was written.

<promise>FAIL</promise>
