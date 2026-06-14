# OpenSpec Capability: git-push-action

## ADDED Requirements

### Requirement: git-push-action pushes the current branch to a remote

The `mohist/push` workflow action SHALL push the current git branch to a configured remote without invoking an AI agent. The action SHALL treat push failure as a terminal task failure.

#### Scenario: Default push to origin
- **WHEN** the `mohist/push` action executes with no `remote` input
- **THEN** it SHALL push the current branch to `origin`
- **AND** it SHALL report task success when the remote accepts the push

#### Scenario: Push to configured remote
- **WHEN** the `mohist/push` action executes with `remote: upstream`
- **THEN** it SHALL push the current branch to the configured remote name
- **AND** it SHALL report task success when that remote accepts the push

#### Scenario: Push target branch
- **WHEN** the `mohist/push` action executes with `target: master`
- **THEN** it SHALL push the local `master` branch to the configured remote
- **AND** it SHALL report task success when the remote accepts the push

#### Scenario: Push failure is terminal
- **WHEN** the `mohist/push` action executes and the remote rejects the push
- **THEN** it SHALL report task failure
- **AND** it SHALL include the git error output in the task failure evidence

#### Scenario: Pure git operation
- **WHEN** the `mohist/push` action executes
- **THEN** it SHALL perform only git remote operations
- **AND** it SHALL NOT start or invoke an AI agent session

### Requirement: git-push-action is registered in the runner action registry

The runner action registry SHALL expose `mohist/push` as an action usable from workflow definitions.

#### Scenario: Registry exposes push action
- **WHEN** the runner loads the action registry
- **THEN** it SHALL include an action named `mohist/push`
- **AND** that action SHALL be selectable by workflow task definitions
