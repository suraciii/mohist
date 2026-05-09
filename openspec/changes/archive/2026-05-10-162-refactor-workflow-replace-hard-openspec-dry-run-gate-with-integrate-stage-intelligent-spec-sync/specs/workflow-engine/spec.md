## ADDED Requirements

### Requirement: REQ-WFE-005 Intelligent spec sync resolves obvious delta classification mistakes

The workflow engine SHALL provide an intelligent OpenSpec sync path for `integrate:spec-sync` that can absorb obvious requirement-level delta classification mistakes while preserving strict validation. At minimum, when a `MODIFIED` requirement has no matching source requirement in the main spec, has no rename ambiguity, and does not duplicate an existing target requirement, the sync path SHALL apply it as an added requirement and record the correction.

#### Scenario: Modified requirement is applied as added when source is absent
- **WHEN** `integrate:spec-sync` processes a `MODIFIED` requirement
- **AND** the main spec has no matching source requirement
- **AND** no rename maps to that source
- **AND** the target requirement name does not already exist
- **THEN** the sync SHALL add the requirement to the main spec
- **AND** the sync output SHALL record a correction from `modified` to `added` with capability, requirement, and reason

#### Scenario: Ambiguous or destructive deltas still fail
- **WHEN** `integrate:spec-sync` processes a missing-source `REMOVED` or `RENAMED FROM` requirement
- **THEN** the sync SHALL fail with structured conflict output
- **AND** it SHALL NOT silently delete, rename, or invent source requirements

### Requirement: REQ-WFE-006 Post-sync main spec validation is mandatory

After intelligent sync resolves delta intent, the workflow engine SHALL validate the candidate main spec before writing or landing it. Invalid results, duplicate requirement headers, missing scenarios, malformed delta sections, or parse-back mismatches SHALL fail `integrate:spec-sync` with structured output.

#### Scenario: Invalid resolved spec is not written
- **WHEN** intelligent sync produces a candidate main spec with duplicate headers, missing scenarios, malformed structure, or parse-back mismatch
- **THEN** `integrate:spec-sync` SHALL fail
- **AND** the invalid result SHALL NOT be silently landed in the main specs
- **AND** the output SHALL include validation errors
