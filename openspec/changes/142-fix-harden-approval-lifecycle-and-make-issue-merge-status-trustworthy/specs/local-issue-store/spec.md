## MODIFIED Requirements

### Requirement: REQ-STORE-001 False-done issues remain detectable

The local issue store SHALL preserve raw `stage`, `status`, and `mergeState` values so false-done issues can be detected instead of silently treated as merged.

#### Scenario: Historical false-done row
- **GIVEN** an existing issue row has `stage=done`
- **AND** `status=completed`
- **AND** `merge_state` is null
- **WHEN** the issue is loaded from storage
- **THEN** the loaded issue SHALL preserve `mergeState` as null or undefined
- **AND** lifecycle helpers SHALL classify it as false-done

### Requirement: REQ-STORE-002 Archive avoids silently hiding false-done issues

Archive flows SHALL NOT silently batch archive completed issues whose merge state is not trustworthy.

#### Scenario: Batch archive skips false-done issue
- **GIVEN** an issue has `stage=done`
- **AND** `status=completed`
- **AND** `mergeState` is null or not `merged`
- **WHEN** archive-all-completed runs
- **THEN** the issue SHALL NOT be silently archived as completed merged work
- **AND** the user SHALL receive a warning or summary that the issue was skipped or requires attention

#### Scenario: Single archive warns or blocks false-done issue
- **GIVEN** an issue has `stage=done`
- **AND** `status=completed`
- **AND** `mergeState` is null or not `merged`
- **WHEN** the user archives that issue directly
- **THEN** the operation SHALL return either a clear warning or a blocking error explaining that the issue is done but not merged
