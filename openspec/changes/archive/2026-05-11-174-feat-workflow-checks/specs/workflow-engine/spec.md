## MODIFIED Requirements

### Requirement: Collect-first check phase reporting

The workflow engine SHALL run all checks in the current phase once in declared order before deciding how to handle failures. The initial phase result set SHALL preserve the complete diagnostic picture for the phase instead of stopping at the first non-pass result.

#### Scenario: Multiple ordinary failures are all visible

- **WHEN** a phase contains multiple non-approval checks
- **AND** more than one of those checks returns `fail` or `error`
- **THEN** the workflow SHALL record all initial check results from that phase run
- **AND** the user-visible phase result SHALL include the later failures instead of stopping at the first one only

#### Scenario: Baseline results are preserved before repair

- **WHEN** the initial phase run finds one or more failed or errored non-approval checks
- **THEN** the workflow SHALL persist the collected baseline check results before fix-task handling begins
- **AND** later rechecks MAY append newer results for the same check without erasing the original phase diagnosis

### Requirement: Approval pending remains non-repairable

`user-approval` SHALL remain a read-only check over existing approval state and SHALL NOT become a repair target. The workflow engine SHALL treat approval pending as a local awaiting-approval outcome only after ordinary non-approval failures have been cleared.

#### Scenario: Pending approval pauses without repair

- **WHEN** `user-approval` returns `pending`
- **AND** no non-approval check in the effective phase result set is failing or errored
- **THEN** the workflow SHALL stop in awaiting approval
- **AND** it SHALL NOT run a fix task for `user-approval`

#### Scenario: Approval does not mask ordinary failures

- **WHEN** `user-approval` returns `pending`
- **AND** another non-approval check in the same phase result set returns `fail` or `error`
- **THEN** the workflow SHALL treat the phase as a repair-or-fail path rather than awaiting approval
- **AND** it SHALL NOT request or refresh approval until the ordinary failures are resolved

#### Scenario: Rejected approval is not repairable

- **WHEN** `user-approval` returns `fail` because approval state is rejected
- **THEN** the workflow SHALL keep that result as visible evidence
- **AND** it SHALL NOT map the approval rejection to a check repair task
