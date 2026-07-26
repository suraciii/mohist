## Context

WorkflowRun currently derives retryable work in two paths: `RetryTarget` feeds the status view, while `Retry` switches on the current failure and independently locates the task or check. The paths differ for persisted `ContextExhaustion` failures, so the UI can omit a retry that execution accepts.

Recovery continuation state also has two budget-bound implementations. The runner already clamps `recoveryRemaining` during recovery evaluation; Server follow-up projection calls `TaskRun.ValidateContinuation`, which rejects values outside the declaration. The Workflow boundary requires Server to transport execution state and Runner to interpret it.

This change affects the Workflow domain, Server status mapping and follow-up ingestion, and existing Runner recovery evaluation. It must preserve the existing retry, rerun, and manual-retry semantics without changing public APIs or persisted shapes.

## Goals / Non-Goals

**Goals:**

- Resolve retryable failed work once and share that result between available-actions mapping and retry execution.
- Treat both ordinary task failures and persisted context-exhaustion failures as task retry targets when a failed task can be identified.
- Retain Server validation of recovery continuation shape while moving declared-budget range interpretation exclusively to the Runner.
- Cover the cross-boundary behavior with focused Server and Runner tests.

**Non-Goals:**

- Change recovery budgets, handler selection, `retrySelf`, manual retry round reset, rerun, or rerun-from-stage behavior.
- Change recovery declaration persistence, wire format, or database schema.
- Address the separate self-retry raw-declaration preservation work tracked by issue 465.

## Decisions

### Use a retry-target resolver as the execution and presentation contract

Keep retry-target selection on `WorkflowRun` and evolve it to represent the operation to perform, rather than only mirroring the original failure reason. It resolves a failed task for ordinary task failures and legacy context-exhaustion failures, falling back to the failed task in the recorded stage when necessary; it resolves a named check for check failures. `Retry` consumes this target to invoke the appropriate stage operation, and `WorkflowStatusMapper` consumes the same target to add the retry action and title.

Rationale: the target is the shared business fact. It prevents failure-reason branches from being copied into presentation and mutation paths.

Alternative considered: add `ContextExhaustion` independently to the status mapper. Rejected because it preserves two implementations that can drift again.

Alternative considered: expose a boolean retryable predicate and retain separate target lookup in execution. Rejected because the boolean cannot prove that the executable target is the one shown to users.

### Keep Server continuation validation structural

Retain validation that a recovery follow-up has a recovery declaration and an explicit numeric continuation value, and that a non-recovery follow-up carries no continuation value. Remove only the negative and above-declared-budget checks from `TaskRun.ValidateContinuation` or replace that method with an equivalently narrow structural validation.

Rationale: presence and declaration association protect the transport contract; bounds determine recovery behavior and belong to the Runner, which already clamps values before recovery selection.

Alternative considered: keep Server range rejection as defense in depth. Rejected because rejecting and clamping produce competing semantic authorities and make future changes require synchronized edits across the boundary.

Alternative considered: normalize values in Server before persistence. Rejected because it silently changes runner-authored execution state and duplicates the Runner decision.

### Preserve the runner clamp and test the boundary explicitly

Do not alter runner `tryRecovery` budget initialization or clamping. Update Server workflow recovery specs so negative and above-budget numeric continuation values are inserted and dispatched, then use Runner recovery tests to demonstrate they respectively evaluate as zero and as the declared budget. Keep malformed shape cases rejected at Server ingestion.

Rationale: this verifies the single authority without requiring a new protocol or cross-process test harness.

Alternative considered: add a new validation endpoint or failure classification for out-of-range values. Rejected because out-of-range numeric values have defined runner semantics and do not need a new user-visible protocol.

## Risks / Trade-offs

- [A malformed or buggy runner can submit an extreme numeric continuation value] -> Server persists and dispatches the value, but Runner clamps it before automatic recovery selection; shape validation still rejects missing or mismatched continuation state.
- [A retry resolver could differ from current-stage mutation preconditions] -> Make `Retry` consume the resolver result only after retaining failed-run and failed-stage guards; add task, check, legacy, and no-target scenarios.
- [Legacy persisted context-exhaustion failures can lack a task id] -> Resolve from the last failed task in the recorded failure stage; when none exists, expose no retry and retain the current actionable rejection.
- [Rollback restores Server range rejection] -> Normal runner-generated continuations are within range, so rollback is safe for normal traffic; an exceptional queued out-of-range continuation can be rejected and retried after redeploying the change.

## Migration Plan

1. Refactor the WorkflowRun retry resolver and retry method, then update status mapping to consume the same resolver result.
2. Narrow Server recovery continuation validation to transport shape and remove the declared-budget range rejection.
3. Update Server Workflow retry and follow-up specs; retain or extend Runner recovery clamp tests.
4. Run the focused Server and Runner suites, then the required Server test suite.

No data migration, schema migration, API change, or coordinated deployment is required. The Runner already implements the target clamp behavior, so Server and Runner can be deployed in either order. To roll back, redeploy the preceding Server version; no persisted data requires reversal.

## Open Questions

None. The issue fixes the authority boundaries without changing product semantics.
