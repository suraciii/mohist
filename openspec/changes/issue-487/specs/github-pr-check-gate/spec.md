### Requirement: Synchronize only an approved review candidate
The `mohist/github-pr` check stage SHALL synchronize the issue branch with the latest repository base only after AI review has passed. An AI review failure SHALL run its existing repair and re-review path without rebasing or otherwise synchronizing the branch with the repository base.

#### Scenario: Failed AI review is repaired without base synchronization
- **WHEN** AI review reports `FAIL`
- **THEN** the workflow SHALL repair the review findings and repeat AI review without rebasing the issue branch onto the repository base

#### Scenario: Passed AI review synchronizes the candidate
- **WHEN** AI review reports `PASS`
- **THEN** the workflow SHALL rebase the issue branch onto the latest configured repository base before publishing the candidate PR head

#### Scenario: Candidate already contains the latest base
- **WHEN** AI review reports `PASS` and the issue branch is already based on the latest repository base
- **THEN** the synchronization step SHALL complete without changing the candidate branch history

### Requirement: Recover rebase conflicts before check verification
The check stage SHALL use the existing rebase-conflict recovery path when synchronization conflicts. It MUST resolve the in-progress rebase before publishing or verifying PR checks, and it MUST preserve the resolved rebase rather than retrying the rebase action after resolution.

#### Scenario: Synchronization encounters a rebase conflict
- **WHEN** the post-review rebase reports a conflict
- **THEN** the workflow SHALL run the existing rebase-conflict resolution task and SHALL NOT begin PR check verification before that resolution completes

#### Scenario: Conflict resolution completes
- **WHEN** the conflict-resolution task completes the in-progress rebase
- **THEN** the workflow SHALL continue by publishing the resolved candidate branch without re-running the rebase action

### Requirement: Verify the published synchronized PR head
After a successful post-review synchronization, the check stage SHALL publish that candidate head, mark the existing PR ready, and verify checks for that PR. Check verification MUST apply to the same synchronized head that was published, and check-stage approval MUST NOT be requested until publishing, readying the PR, and verification have all completed successfully.

#### Scenario: Synchronized candidate is published and verified
- **WHEN** the post-review synchronization completes successfully
- **THEN** the workflow SHALL push the synchronized candidate head, mark the existing PR ready, and verify that PR's checks in that order

#### Scenario: Verification has not completed
- **WHEN** publishing or PR check verification has not completed successfully
- **THEN** the workflow SHALL NOT request check-stage approval

### Requirement: Require non-empty passing PR checks
PR check verification SHALL pass only when the current PR head has a non-empty check set, no reported check is pending, and no reported check has failed. When the check set is empty, verification MUST continue polling for a bounded wait; if it remains empty at the end of that wait, verification MUST fail with an actionable checks-unavailable result.

#### Scenario: Current PR head has passing checks
- **WHEN** the current PR head reports one or more checks and every reported check has completed without failure
- **THEN** PR check verification SHALL succeed

#### Scenario: Current PR head has pending or failed checks
- **WHEN** the current PR head reports a pending check or a failed check
- **THEN** PR check verification SHALL NOT succeed

#### Scenario: Checks have not appeared before the bounded wait ends
- **WHEN** the current PR head continues to report an empty check set until the bounded wait expires
- **THEN** PR check verification SHALL return an actionable `pr-checks-unavailable` failure and SHALL NOT treat the empty check set as passing

### Requirement: Retain final integration protection
The integrate stage SHALL retain its final merge protection, including recovery when the repository base moves after check-stage approval and handling when branch protection changes before merge.

#### Scenario: Base moves after check approval
- **WHEN** the repository base moves after check-stage approval and the merge is rejected because the base moved
- **THEN** integrate SHALL synchronize the branch, publish the updated PR head, and retry the merge through its existing base-moved recovery path

#### Scenario: Branch protection blocks merge after check approval
- **WHEN** branch protection prevents the approved PR from merging during integrate
- **THEN** integrate SHALL retain its existing merge protection handling and SHALL NOT treat earlier check-stage approval as merge authorization
