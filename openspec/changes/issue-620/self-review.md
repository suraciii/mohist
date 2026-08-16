# Self-Review: issue-620

## Review Mode

This is the first review. `self-review.md` did not exist before this review, so I performed the required full sweep. I re-read issue 620 with `mo issue view 620 --project proj_f6c141d63b6243bfbb481737b2243b87` before reviewing the artifacts.

The issue acceptance criteria are:

1. A Retry button on a failed notification completes one retry with the clicker's permissions and CLI-equivalent effect.
2. Expired, tampered, or other-operator buttons are rejected with a visible message.
3. An ambiguous multi-Bot message gets an interactive choice, and selecting one Agent starts work only for that Agent.
4. Button clicks do not cause duplicate execution under redelivery.

## Must-Fix Findings

### MF-001 — Root Retry has no defined fresh-attempt idempotency boundary

`design.md:65` requires a fresh input/turn and a retry-specific idempotency key, while `specs/slack-failure-retry-action/spec.md:31-39` requires a new attempt for both root and threaded failures. The current root launch boundary cannot provide that as described: `packages/server/src/Mohist.Server/Agent/Services/AgentLauncher.cs:413-444` hard-codes `LaunchConnectionAsync`'s coordinator key to `slack:{workspace}:{conversation}:{message}`. Reusing that path for Retry addresses the original launch coordinator and returns its existing failed plan/identities; it does not create a fresh attempt. Using a different key is not expressible through this method, and would also need an explicit decision about whether root Retry creates a new Session or a new input/turn in the existing Session, since the design says the latter.

T-002 requires a new root identity in its acceptance criteria (`tasks.json` T-002, criterion 3) but does not add or define a launcher/session contract that supplies it. The plan must specify the root Retry operation boundary, its idempotency key and identity adoption, and its Session/provenance behavior before implementation. Leaving this unresolved makes a valid root Retry fail the issue's first acceptance criterion.

### MF-002 — Write-before-dispatch recovery is promised but has no executable mechanism

`design.md:54-63` commits a `SlackRetryOperations` row before dispatch and says a later replay/recovery call will resume the same dispatch key. `design.md:126` and `specs/slack-failure-retry-action/spec.md:42-55` make that crash/failover behavior normative. However, neither T-001 nor T-002 defines a recovery worker, reminder, startup sweep, operation re-entry endpoint, or the exact route behavior that lets a committed `dispatch-pending` operation resume after the process dies before the dispatcher is called.

This is especially important because the existing provider inbox deduplicates an action identity at `SlackProviderInboxStore.AcceptAsync` (`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackProviderInboxStore.cs:75-81`), and the current interaction route suppresses a replay presentation after a replayed result (`packages/server/src/Mohist.Server/Api/SlackInteractionRoutes.cs:62-79`). A redelivery can therefore stop at the receipt fence unless the new plan explicitly makes the operation recovery path reachable on replay. A persisted row alone cannot complete the Retry after the adapter has already acknowledged the interaction. Add the recovery mechanism, its ordering with provider-inbox receipt and operation claiming, and crash/restart verification. Without it, an accepted click can remain apparently accepted without starting work, violating issue acceptance criteria 1 and 4.

### MF-003 — Retryability is an unresolved product rule that controls acceptance criterion 1

The capability spec requires a Retry control only for a `retryable` failure and forbids it for a non-retryable result (`specs/slack-failure-retry-action/spec.md:1-10`), but `design.md:145` explicitly leaves the exact failure categories as an open question. The issue says the failed notification carries a Retry action; no final mapping is given for which failed terminal notifications qualify. The current system already carries `FailureCategory`, but the plan does not define an authoritative classification or precedence for initial launches, follow-ups, unknown/legacy events, and category-less failures.

This is not merely implementation detail: different answers change whether a failed notification satisfies the issue's first acceptance criterion, and they make T-002's rendering tests non-deterministic. Resolve the category policy in the plan/spec and add a category-to-control test matrix before the plan is build-ready.

## Dimension Checks

### Issue Goals and Acceptance Criteria

**Checked, issue found.** The issue was read before the artifacts. The plan maps all four criteria to capabilities and tasks, but MF-001 and MF-003 leave the Retry criterion incomplete, and MF-002 leaves its redelivery/crash behavior incomplete.

### Coverage

**FAIL due to the must-fix findings.** The mapping is otherwise present:

| Issue criterion | Plan coverage | Review result |
|---|---|---|
| Retry has CLI-equivalent effect | Retry spec and T-002 | Root dispatch boundary and retryability policy are unresolved (MF-001, MF-003). |
| Invalid/expired/unauthorized action is visibly rejected | Shared action boundary, Retry spec, selection spec, T-001/T-002/T-003 | Covered. |
| One selected Bot alone starts work | Selection spec and T-003 | Covered in the intended state machine. |
| Redelivery cannot duplicate execution | Retry/selection operation fences and T-002/T-003 tests | Recovery/re-entry after a pre-dispatch commit is unspecified (MF-002). |

### Correctness

**FAIL.** The signing, actor/context checks, conditional single-winner selection, authoritative source snapshot, and outbox presentation approach are directionally correct and consistent with the issue. The root Retry path conflicts with the current launch idempotency contract, and the proposed pre-dispatch fence has no defined recovery executor, so the approach cannot guarantee a completed fresh retry in all stated failure cases.

### Current-Code Consistency

**FAIL due to MF-001 and MF-002.** The plan correctly identifies and reuses the existing Stop, inbox, session follow-up, thread mapping, status projection, outbox, and adapter boundaries. It does not yet define how the new Retry operation fits the current `LaunchConnectionAsync` coordinator key or how a durable operation is resumed after the interaction request is lost. The proposed shared signing and Block Kit pass-through changes otherwise follow local conventions and preserve the existing Stop behavior in the stated acceptance criteria.

### Task Breakdown, Ordering, and Verifiability

**FAIL.** T-001 -> T-002/T-003 is a sensible high-level dependency graph, and the acceptance criteria include concurrency, migration, adapter, and presentation tests. T-002's root and threaded dispatch work is too underspecified to implement against the current launcher, and its crash-recovery test has no corresponding runtime mechanism. The open retryability question also prevents deterministic terminal-rendering tests. These are completeness defects, not merely task wording issues.

## Observations

- `design.md:147` leaves the candidate-count limit open even though Slack Block Kit imposes action-count limits. The text fallback is specified, so this is an operational boundary to settle rather than a must-fix against the issue's stated normal multi-Bot scenario.
- `design.md:146` leaves the Retry/selection lifetime open. Reusing the existing five-minute Stop lifetime is a reasonable default and the issue only requires expiry, so this does not affect the verdict.
- The candidate snapshot is described as bounded for source text and attachments (`design.md:91-95`, `design.md:130`), but no explicit maximum candidate count or serialized row budget is part of the migration/task acceptance criteria.
- T-002 and T-003 both depend only on T-001 while likely changing shared interaction routing, action result presentation, and common persistence/composition code. The work is feasible, but the plan would benefit from explicit ownership or a serial integration step to avoid conflicting edits.

<promise>FAIL</promise>
