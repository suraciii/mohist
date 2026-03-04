## ADDED Requirements

### Requirement: PR Creation

The system SHALL create Pull Requests for Issue implementation.

#### Scenario: Create Draft PR in design stage

- **WHEN** entering design stage
- **THEN** system SHALL create a Draft PR
- **AND** PR title SHALL reference the Issue number

#### Scenario: Branch naming

- **WHEN** creating a PR
- **THEN** branch name SHALL follow pattern: issue-{N}-{short-description}
- **AND** branch SHALL be created from the default branch

### Requirement: PR State Transitions

The system SHALL manage PR state transitions.

#### Scenario: Draft to Open transition

- **WHEN** implementation stage completes
- **THEN** system SHALL convert PR from Draft to Open
- **AND** system SHALL mark PR as "Ready for Review"

#### Scenario: Merge on approval

- **WHEN** both agent and user review approve the PR
- **THEN** system SHALL merge the PR
- **AND** system SHALL use squash merge by default

### Requirement: Single PR for Design and Implementation

Design and implementation SHALL be in the same PR.

#### Scenario: Add specs to PR

- **WHEN** design stage produces specs
- **THEN** specs SHALL be committed to the same PR
- **AND** PR SHALL remain in Draft state

#### Scenario: Add implementation to PR

- **WHEN** implementation stage produces code
- **THEN** code SHALL be committed to the same PR
- **AND** PR SHALL transition to Open state

### Requirement: PR Description

PR description SHALL include required information.

#### Scenario: Include Issue reference

- **WHEN** creating a PR
- **THEN** PR body SHALL reference the Issue
- **AND** PR body SHALL include "Closes #{issue-number}"

#### Scenario: Include spec format note

- **WHEN** not using OpenSpec format
- **THEN** PR body SHALL explain the spec format
- **AND** PR body SHALL document any deviations from standard

### Requirement: PR Cleanup

The system SHALL clean up PRs on failure or cancellation.

#### Scenario: Close PR on persistent failure

- **WHEN** Issue processing fails persistently (blocked)
- **THEN** system SHALL close the PR
- **AND** system SHALL add an explanation comment

#### Scenario: Delete branch on PR close

- **WHEN** closing a PR
- **THEN** system SHALL delete the feature branch
- **AND** system SHALL ensure no orphaned branches remain
