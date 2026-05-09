## ADDED Requirements

### Requirement: REQ-PM-003 CHECK defers recoverable OpenSpec sync conflicts

CHECK SHALL NOT hard-block issue progression solely because OpenSpec sync preview detects a recoverable delta classification conflict such as `missing_source` for a requirement written under `MODIFIED Requirements`. CHECK MAY record read-only preview evidence, but durable updates to `openspec/specs/` SHALL remain an INTEGRATE responsibility.

#### Scenario: Missing source preview does not block CHECK
- **WHEN** CHECK runs OpenSpec sync preview for a change delta
- **AND** the preview reports `missing_source` for a `MODIFIED` requirement that may be resolved during integration
- **THEN** CHECK SHALL NOT fail solely because of that preview conflict
- **AND** CHECK SHALL NOT write to `openspec/specs/`
- **AND** the preview evidence, if collected, SHALL remain visible as advisory output

#### Scenario: Non-OpenSpec CHECK gates still block
- **WHEN** CHECK runs health, merge readiness, AI review, or user approval checks
- **THEN** those checks SHALL retain their existing blocking semantics

### Requirement: REQ-PM-004 Integrate spec sync failure remains local

When `integrate:spec-sync` fails, the workflow SHALL keep the issue at INTEGRATE or an interrupted/blocked-at-INTEGRATE state with visible failure evidence. The workflow SHALL NOT automatically fall back to PLAN, BUILD, or CHECK, and SHALL NOT automatically rerun the entire pipeline.

#### Scenario: Spec sync failure stops at INTEGRATE
- **WHEN** `integrate:spec-sync` fails due to sync resolution or validation
- **THEN** the issue SHALL remain associated with INTEGRATE failure state
- **AND** `integrate:archive-change`, `integrate:merge`, and `final-health` SHALL NOT run
- **AND** the failure output SHALL identify the failing step as `integrate:spec-sync`
