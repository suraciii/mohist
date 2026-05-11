## MODIFIED Requirements

### Requirement: ai-review task artifact contract

The workflow engine SHALL treat `ai-review` as the CHECK-stage task that produces the final review artifact for the current candidate snapshot. The task SHALL complete only when `review.md` exists, has the expected review format, contains a machine-readable verdict, and represents the current code snapshot.

#### Scenario: Missing review artifact fails ai-review task

- **WHEN** `ai-review` finishes execution
- **AND** `review.md` is missing after allowed retries
- **THEN** the `ai-review` task SHALL fail
- **AND** the workflow SHALL NOT create a separate user-visible check for missing review artifacts

#### Scenario: Unparseable verdict fails ai-review task

- **WHEN** `ai-review` produces `review.md`
- **AND** the verdict is missing or cannot be parsed
- **THEN** the `ai-review` task SHALL fail
- **AND** `review-passed` SHALL NOT be reported as the ordinary failing user-visible check for that artifact error

#### Scenario: Valid review artifact enables review-passed

- **WHEN** `ai-review` produces a valid final `review.md` with a parseable verdict
- **THEN** `review-passed` SHALL read that verdict as check evidence

### Requirement: review-passed dynamic repair

The workflow engine SHALL use `review-passed` as the read-only verifier for the final review verdict. When `review-passed` fails because the review verdict is FAIL, the engine SHALL create actual repair work from the review findings, rerun `ai-review`, and then rerun `review-passed` against the regenerated final review.

#### Scenario: Failed review creates actual repair task

- **WHEN** `review-passed` reads a FAIL verdict with repairable findings
- **THEN** the workflow SHALL create and run a concrete repair task based on those findings
- **AND** it SHALL NOT rely on a predeclared empty fix task that was visible before the failure occurred

#### Scenario: Repair invalidates old review

- **WHEN** review repair changes code or review-relevant artifacts
- **THEN** existing CHECK-stage review artifacts and review checkpoints SHALL be invalidated
- **AND** `ai-review` SHALL rerun before `review-passed` is evaluated again

#### Scenario: Re-review remains the approval truth

- **WHEN** repair is followed by a regenerated review
- **THEN** the regenerated review SHALL be the current review truth for approval
- **AND** stale review verdicts from earlier snapshots SHALL NOT be used for approval

### Requirement: merge-ready invalidates review on code change

The workflow engine SHALL use `merge-ready` as the read-only user-visible verifier that the reviewed candidate can be integrated into the target branch. If merge-readiness work changes the candidate code snapshot, the workflow SHALL invalidate the existing review result and rerun `ai-review` before approval.

#### Scenario: Merge-ready passes without snapshot change

- **WHEN** the reviewed candidate can be merged into the target branch without changing the candidate snapshot
- **THEN** `merge-ready` SHALL pass
- **AND** the current `review-passed` result MAY remain valid for approval

#### Scenario: Merge-ready records mergeability failure

- **WHEN** the reviewed candidate cannot currently be merged into the target branch
- **THEN** `merge-ready` SHALL fail with target branch and conflict or mergeability evidence
- **AND** the workflow SHALL NOT expose the legacy `merge-readiness` check name for that decision

#### Scenario: Merge repair changes snapshot

- **WHEN** merge-readiness repair, rebase, or conflict resolution changes `HEAD`
- **THEN** the current review result SHALL be invalidated
- **AND** `ai-review` SHALL rerun for the new snapshot
- **AND** approval SHALL NOT be requested until `review-passed` and `merge-ready` both pass for that snapshot
