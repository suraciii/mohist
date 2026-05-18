# Check Deep Review And Batch Fix

Tracked by issue #230: `fix(check): batch deep review findings and converge repair cycles`.

## Problem

Mohist has repeatedly hit near-infinite review-fix ping-pong in Check. The user-visible symptom is not just that review fails; it is that every fix changes the candidate, triggers another full review, and the next review discovers a new reasonable problem. Users cannot tell whether the issue is converging or whether Mohist has entered self-review churn.

This was visible again while progressing issue #226. Review rounds successively found:

- missing regression coverage for stale failed sessions coexisting with later successful workflow evidence;
- legacy Build runner source-state classification gaps for missing and invalid `tasks.json`;
- stale successful Build source evidence not being replaced by later missing, invalid, or empty source evaluation.

Each finding was plausible and in scope. The problem is that Mohist found them across multiple review-fix cycles instead of making the first review pass more comprehensive and using a batch repair plan.

## Desired Product Shape

Check should favor:

```text
deep review once
  -> batch all blocking findings
  -> batch fix
  -> focused verification review
```

over:

```text
shallow review
  -> fix one finding
  -> full review again
  -> discover next finding
  -> repeat
```

The first review should deliberately continue after the first blocking finding and enumerate likely acceptance-criteria failures, nearby edge cases, and missing regression coverage. Later review passes should verify the finding batch and only add new blockers when the new problem is a fix-introduced regression, a missed issue acceptance criterion, or a high-risk safety/data concern.

## Key Model

```text
Candidate
  reviewedSha
  changedFiles
  acceptanceCriteria

DeepReview
  findings[]
    id
    severity: blocking | follow-up
    scope: acceptance-criteria | regression | security | follow-up
    area
    evidence
    suggestedFix
    verification

FixBatch
  findingIds[]
  fixCommit
  verificationCommands
  resolutionStatus[]

VerificationReview
  verifies findingIds[]
  does not restart open-ended review by default
```

## Proposed Flow

```text
Build candidate
  -> Freeze candidate metadata
  -> Deep review pass
       - read issue acceptance criteria
       - inspect all changed files
       - inspect adjacent workflow/retry/artifact paths likely affected
       - enumerate all blocking findings instead of stopping at first failure
       - classify follow-up findings separately
  -> Review plan complete check
  -> Batch fix blocking findings
       - trivial issues may be fixed inside review if safe and documented
       - local blocking findings go to one fix batch
       - large/design findings become dedicated tasks or follow-up issues
  -> Resolution review
       - verify all blocking finding IDs were resolved
       - verify fix evidence and tests
       - avoid open-ended rediscovery unless the issue remains acceptance-critical
  -> health:check
  -> merge-ready
  -> user approval
```

## Boundaries

This is related to but distinct from:

- #186, which focuses on preserving review history and rerunning ai-review after autofix;
- #204, which focuses on binding review artifacts to the reviewed code snapshot.

This exploration focuses on review depth, finding batching, fix batching, and convergence policy.

## Acceptance Signals

- The first Check review is expected to find a batch of blocking findings, not stop at the first one.
- Review output is structured enough for a fix agent to repair all blocking findings in one batch.
- Review findings have stable IDs and explicit scope/severity.
- Follow-up findings are visible but do not block the current issue unless they violate the current acceptance criteria or introduce serious risk.
- After fix, verification review primarily checks resolved finding IDs instead of restarting unlimited full review.
- The user can see how many review-fix cycles occurred, what was fixed, and why the issue is still blocked if it remains blocked.
