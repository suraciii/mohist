## MODIFIED Requirements

### Requirement: workflow health gate policy

Workflow configuration SHALL support explicit per-boundary health gate policies for `plan`, `build`, `check`, and `postMerge`. Each policy SHALL include enabled/disabled state, command, timeout, auto-fix behavior, max fix attempts, and fallback reaction.

#### Scenario: Default health gate policies
- **WHEN** no workflow health gate configuration is present
- **THEN** the system SHALL resolve enabled default policies for plan, build, check, and postMerge
- **AND** plan SHALL default to `npm run typecheck`
- **AND** build SHALL default to `npm run build`
- **AND** check SHALL default to `npm run build && npm test`
- **AND** postMerge SHALL default to the check gate command unless explicitly configured

#### Scenario: Per-stage health gate override
- **WHEN** `workflow.yaml` defines `healthGates.build.command`, `timeout`, `autoFix`, `maxFixAttempts`, or `fallbackReaction`
- **THEN** the resolved build health gate SHALL use those configured values
- **AND** unspecified fields SHALL fall back to defaults field-by-field

#### Scenario: Disabled health gate is explicit policy
- **WHEN** `workflow.yaml` defines a health gate with `enabled: false`
- **THEN** the resolved policy SHALL preserve `enabled: false`
- **AND** stage execution SHALL be able to record that the gate was skipped by policy

### Requirement: checks.buildTest compatibility

Existing workflow configurations using `checks.buildTest` SHALL continue to configure the check-stage full verification gate when no explicit `healthGates.check` policy is present.

#### Scenario: checks.buildTest maps to check health gate
- **WHEN** `workflow.yaml` defines `checks.buildTest.command`, `timeout`, `autoFix`, or `maxFixAttempts`
- **AND** `healthGates.check` is absent
- **THEN** the resolved `check` health gate SHALL use the corresponding `checks.buildTest` values

#### Scenario: Explicit healthGates.check takes precedence
- **WHEN** both `checks.buildTest` and `healthGates.check` are present
- **THEN** the resolved `check` health gate SHALL use `healthGates.check`
- **AND** `checks.buildTest` SHALL remain available only for legacy consumers
