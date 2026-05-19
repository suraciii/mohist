## MODIFIED Requirements

### Requirement: REQ-PM-STRUCTURED-001 pipeline stage state exposes generic convergence status

Pipeline stage state SHALL expose generic convergence status derived from authoritative structured task, check, and reaction outputs.

#### Scenario: Stage state includes convergence fields

- **WHEN** a stage is blocked by a structured failed check or is recovering through reactions
- **THEN** stage state SHALL include failed check, blocking item count, directly repaired count, reaction attempts, attempted item IDs, resolved item IDs, unresolved item IDs, new blocking item IDs, non-blocking item IDs, and blocked reason
- **AND** these fields SHALL be computed from stored structured outputs rather than parsing messages or artifacts in presentation code

#### Scenario: No convergence state is available

- **WHEN** a stage has no structured failure or older records do not contain structured result data
- **THEN** existing stage-state fields SHALL remain available
- **AND** consumers SHALL NOT be required to infer convergence from prose
