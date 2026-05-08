## MODIFIED Requirements

### Requirement: stage exit health guarantee

Pipeline stage progression SHALL include the configured stage exit health gate as part of the boundary contract. A stage may fail, auto-fix, ask for help, or escalate, but it SHALL NOT silently advance while an enabled health gate is known to be failing.

#### Scenario: Failing health gate blocks stage progression
- **WHEN** a stage finishes its primary work
- **AND** the configured health gate for that stage fails
- **THEN** the issue SHALL remain before the next stage boundary
- **AND** the stage execution SHALL record the health gate failure
- **AND** the workflow SHALL apply the configured reaction instead of silently advancing

#### Scenario: Disabled health gate documents weaker policy
- **WHEN** a health gate is disabled by configuration
- **THEN** the stage MAY advance without running that command
- **AND** the stage execution result SHALL make the disabled policy visible

### Requirement: post-merge health gate before done

Done/completed state SHALL only be reached after merge succeeds and the enabled post-merge health gate passes. Direct merge paths and merge queue paths SHALL use the same final verification rule.

#### Scenario: Merge queue completion requires post-merge health gate
- **WHEN** the merge queue successfully merges an issue branch
- **AND** the post-merge health gate is enabled
- **THEN** the post-merge health gate SHALL run against the merged target project state
- **AND** the issue SHALL become done/completed only if that gate passes

#### Scenario: Direct merge cannot bypass final health gate
- **WHEN** a user invokes the direct merge API for an issue
- **AND** final gates are enabled
- **THEN** the API SHALL run the same post-merge health verification used by the merge queue
- **AND** the API SHALL NOT mark the issue done/completed if the final gate fails

#### Scenario: Post-merge gate disabled
- **WHEN** the post-merge health gate is disabled by configuration
- **THEN** completion MAY proceed after merge succeeds
- **AND** the result SHALL explicitly reflect that final verification used a weaker disabled policy
