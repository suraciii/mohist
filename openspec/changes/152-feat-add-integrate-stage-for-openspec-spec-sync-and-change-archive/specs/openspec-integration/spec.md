## ADDED Requirements

### Requirement: deterministic spec sync
The system SHALL deterministically synchronize approved OpenSpec delta specs from an active change into `openspec/specs/` without using an agent or LLM to interpret requirements.

#### Scenario: Preview delta application
- **WHEN** Check evaluates an active OpenSpec change
- **THEN** the system previews the delta application in memory
- **AND** reports affected capabilities, target files, added/modified/removed/renamed counts, and conflicts
- **AND** does not modify `openspec/specs/`

#### Scenario: Apply approved deltas
- **WHEN** Integrate runs after Check approval
- **AND** delta validation passes
- **THEN** the system applies the delta specs to `openspec/specs/<capability>/spec.md`
- **AND** records a spec sync summary

### Requirement: deterministic requirement delta validation
The system SHALL validate requirement-level delta operations before writing any main spec files.

#### Scenario: Missing source requirement blocks sync
- **WHEN** a MODIFIED, REMOVED, or RENAMED FROM requirement is not present in the target main spec
- **THEN** spec sync fails with the capability and requirement header
- **AND** Integrate does not archive, merge, run final health, or enter Done

#### Scenario: Duplicate target requirement blocks sync
- **WHEN** an ADDED or RENAMED TO requirement would duplicate an existing requirement header
- **THEN** spec sync fails with the capability and requirement header
- **AND** no main spec file is written

#### Scenario: Requirement lacks scenario
- **WHEN** an added, modified, or renamed target requirement has no scenario
- **THEN** spec sync fails with a validation error

### Requirement: spec sync before change archive
The system SHALL archive an OpenSpec change only after its approved delta specs have been synchronized to main specs.

#### Scenario: Sync succeeds before archive
- **WHEN** Integrate successfully applies delta specs
- **THEN** it archives the active change under `openspec/changes/archive/YYYY-MM-DD-<change>/`
- **AND** records the archive path in integration evidence

#### Scenario: Sync failure prevents archive
- **WHEN** spec sync fails
- **THEN** the active change remains in `openspec/changes/<change>/`
- **AND** Integrate is blocked at `spec-sync`

### Requirement: no code fix integrate merge
The system SHALL land approved candidates during Integrate without making product-code or behavior-fix changes.

#### Scenario: Clean merge succeeds
- **WHEN** an approved candidate can be fast-forwarded or cleanly rebased then fast-forwarded into the target branch
- **THEN** Integrate records target branch, base SHA, candidate head SHA, and landed commit truth
- **AND** continues to final health verification

#### Scenario: Merge conflict blocks Integrate
- **WHEN** the candidate cannot be cleanly landed
- **THEN** Integrate fails at `merge`
- **AND** does not invoke agent conflict resolution, build-fix agents, or automatic code modification
- **AND** does not enter Done
