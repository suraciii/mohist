# Self-Review: issue-640

## Verdict

FAIL. This is the first review. One must-fix problem leaves repeated bounded cleanup attempts vulnerable to the same terminal-fact delivery lag that issue 640 requires cleanup admission not to fail on.

## Must-fix findings

### MF-1 — The wait does not cover terminal facts from a preceding cleanup attempt

The proposed wait is keyed only by the Workflow scheduling identity and explicitly covers `workflow-session` and `workflow-cleanup` records (`design.md:74-99`). That is sufficient before the first cleanup attempt: the original task turn's facts are `workflow-session` records, and the cleanup admission boundary is a `workflow-cleanup` record.

It is not sufficient before a second or later cleanup attempt. Once a cleanup boundary is accepted, `WorkflowAgentSessionReporter` emits that cleanup turn's runtime input and terminal activity as `session-followup` records targeted by AgentSession id and keyed by the cleanup turn id, not by project/workflow-run/session-name (`packages/runner/src/actions/workflow-agent-session-reporter.ts:310-341,439-456`). The Server marks the non-launch cleanup turn terminal only when the delayed `session-followup` terminal activity is processed (`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.RuntimeEventDomain.cs:149-181`). If attempt 1 returns while its terminal activity remains retained and the worktree is still dirty, attempt 2's proposed Workflow-keyed wait can complete immediately, then `session.cleanup` reaches a Session that still has an active cleanup turn and is rejected before the cleanup prompt runs.

That violates the issue goal stated in the title — cleanup-turn admission must not fail because the previous turn's convergence facts were delivered late — and the plan's acceptance criteria that:

- a work item's own cleanup follow-up waits for the **previous turn's** terminal facts before admission (`specs/cleanup-turn-admission/spec.md:1-3`);
- the existing maximum cleanup-attempt budget remains usable and task success/failure follows the actual cleanup result (`specs/cleanup-turn-admission/spec.md:46-54`); and
- T-002 preserves maximum-attempt accounting and actual cleanup success/failure (`tasks.json:39-40`).

The plan must define and test admission for attempt 2+ so the wait observes the immediately preceding cleanup turn's retained `session-followup` terminal facts as well as the original Workflow turn. Without that, the bounded cleanup loop can still deterministically fail on delivery lag rather than on the cleanup result.

## Observations

- `awaitWorkflowSessionDelivery` is optional on the port and a missing method intentionally falls back to today's no-wait behavior (`design.md:148-150`; `tasks.json:25,50`). The production outbox is required to implement it, so this does not independently fail the current plan, but making a mandatory admission invariant optional weakens compile-time protection against a future production replacement silently reintroducing the bug.
- The design treats retention-cap drops as delivery completion (`design.md:91-99`), while the capability spec defines completion in terms of acknowledgement or deterministic-refusal settlement. Existing retention only drops reconstructible streaming deltas, so this does not defeat terminal convergence, but the implementation contract and spec should use consistent terminology.

## Review dimensions

### Issue basis — checked

The issue was re-read before the artifacts. The issue record has no body or separately enumerated acceptance criteria; its authoritative goal is the title: Workflow cleanup-turn admission must not fail because the preceding turn's convergence facts arrive late. The plan's own specs make that goal concrete for both runtimes, bounded waits, preserved cross-attempt fail-closed behavior, unchanged cleanup limits, and actual-result-based completion.

### Coverage — must-fix issue found

The first cleanup attempt is covered for OpenCode and Pi, as are timeout evidence, already-delivered facts, and non-cleanup cross-attempt admission. Coverage is incomplete for a later cleanup attempt whose immediately preceding cleanup turn has undelivered `session-followup` terminal facts; see MF-1.

### Correctness — must-fix issue found

Waiting for the Workflow scheduling group correctly serializes the original Workflow turn with the first cleanup boundary. It does not serialize one cleanup turn's Session-scoped terminal facts with the next cleanup boundary, so the approach does not correctly satisfy the general “previous turn” requirement across the existing bounded cleanup loop; see MF-1.

### Consistency with the current codebase — must-fix issue found

The proposed files and admission locations match current Runner conventions, and preserving Server validation is correct. However, the design's assumption that all relevant retained facts collapse under the Workflow scheduling key conflicts with the current reporter's deliberate use of the `session-followup` family for cleanup runtime facts.

### Task breakdown — must-fix issue found

T-001 then T-002 is an ordered, verifiable two-task decomposition for the proposed mechanism. T-002 lacks an acceptance case and regression test for attempt 2+ under delayed cleanup-terminal delivery. Its broad “actual cleanup success/failure” test requirement does not identify the distinct key-family transition that causes MF-1.

<promise>FAIL</promise>