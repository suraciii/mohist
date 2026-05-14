## MODIFIED Requirements

### Requirement: Check merge-ready uses squash merge semantics

The Check-stage `merge-ready` gate SHALL pass only when the current issue candidate can be squash-merged into the current base branch using Mohist's final Integrate merge semantics.

#### Scenario: Conflicting squash merge fails merge-ready

- **GIVEN** an issue candidate whose normal issue worktree has no active rebase conflict files
- **AND** the same candidate would fail `git merge --squash <candidate>` against the current base branch
- **WHEN** the Check stage runs `merge-ready`
- **THEN** `merge-ready` SHALL fail
- **AND** the output SHALL include structured mergeability facts including `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `targetBranch`, `strategy`, `canMerge`, and `conflictFiles`

#### Scenario: Clean squash merge passes merge-ready

- **GIVEN** an issue candidate that can be cleanly squash-merged into the current base branch
- **WHEN** the Check stage runs `merge-ready`
- **THEN** `merge-ready` SHALL pass
- **AND** the pass decision SHALL be based on `canMerge: true` from the squash mergeability preflight

### Requirement: Check approval validates mergeability snapshot freshness

Check approval SHALL be bound to the passing merge-ready snapshot that was presented for approval and SHALL reject stale or missing mergeability evidence before enqueueing Integrate.

#### Scenario: Approval rejects stale merge-ready evidence

- **GIVEN** Check produced a passing `mergeReadySnapshot`
- **AND** the base branch, candidate head, merge base, target branch, or `canMerge` value no longer matches the current Git state
- **WHEN** the user approves Check
- **THEN** Mohist SHALL reject the approval as stale
- **AND** Mohist SHALL ask for Check to be rerun instead of approving a different candidate than the one presented

#### Scenario: Approval rejects missing merge-ready evidence

- **GIVEN** Check approval output has no valid passing `mergeReadySnapshot`
- **WHEN** the user approves Check
- **THEN** Mohist SHALL reject the approval before enqueueing Integrate

### Requirement: Integrate preflights before side effects

Integrate SHALL validate or refresh mergeability evidence before running side-effectful delivery steps such as spec sync, change archive, or the final merge.

#### Scenario: Integrate stops before side effects on stale evidence

- **GIVEN** approved mergeability evidence is missing or stale when Integrate starts
- **WHEN** Integrate validates mergeability before delivery side effects
- **THEN** Integrate SHALL stop before spec sync, archive, or merge side effects if the current candidate cannot be proven mergeable
- **AND** the failure SHALL include structured mergeability evidence or a clear instruction to rerun Check

#### Scenario: Integrate continues with current mergeability evidence

- **GIVEN** approved mergeability evidence still matches current base and candidate state
- **WHEN** Integrate starts
- **THEN** Integrate SHALL continue to existing delivery steps without adding a new user-facing workflow status
