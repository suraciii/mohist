## MODIFIED Requirements

### Requirement: Persist merge-ready evidence for approval and diagnostics

Workflow run records SHALL preserve structured merge-ready output so approval, Integrate, API, CLI, logs, and UI surfaces can display and compare the mergeability evidence used for decisions.

#### Scenario: Check records merge-ready snapshot

- **GIVEN** Check runs the `merge-ready` gate
- **WHEN** the gate completes
- **THEN** the workflow run evidence SHALL include the structured mergeability snapshot with `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `strategy`, `canMerge`, and `conflictFiles`

#### Scenario: Integrate records refreshed preflight diagnostics

- **GIVEN** Integrate runs a fresh mergeability preflight because approved evidence is missing or stale
- **WHEN** the preflight completes
- **THEN** Integrate SHALL record diagnostic output containing the current mergeability snapshot
- **AND** refreshed Integrate diagnostics SHALL NOT silently replace the Check approval evidence as user-approved
