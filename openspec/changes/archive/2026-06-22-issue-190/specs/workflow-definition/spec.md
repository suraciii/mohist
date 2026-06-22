## ADDED Requirements

### Requirement: Built-in mohist/pr definition diverges from mohist/default only in the delivery task

The system SHALL provide a built-in `mohist/pr` workflow definition whose Plan, Build, Check, and Integrate stages — including tasks, approval gates, repair policy, check policy, and invalidation policy — match `mohist/default` exactly. The `mohist/pr` Integrate stage SHALL preserve the same ordered task list (`integrate:spec-sync` → `integrate:archive-change` → `integrate:prepare` → `integrate:publish`) and the same single-push-owner invariant. The ONLY difference SHALL be that the `integrate:publish` task uses the `mohist/publish-via-pr` action instead of `mohist/publish`.

#### Scenario: mohist/pr shares plan/build/check with mohist/default

- **WHEN** the `mohist/pr` workflow definition is loaded
- **THEN** the Plan, Build, and Check stage definitions SHALL match `mohist/default` task-for-task
- **AND** the approval gates, repair policies, check policies, and invalidation policies SHALL match `mohist/default`

#### Scenario: mohist/pr Integrate differs only in the publish action

- **WHEN** the `mohist/pr` Integrate stage is loaded
- **THEN** the ordered tasks SHALL be `integrate:spec-sync`, `integrate:archive-change`, `integrate:prepare`, and `integrate:publish`
- **AND** the `integrate:publish` task SHALL use the `mohist/publish-via-pr` action
- **AND** every other Integrate task SHALL match `mohist/default`

#### Scenario: mohist/pr preserves a single push owner

- **WHEN** the `mohist/pr` Integrate workflow is loaded
- **THEN** `integrate:publish` SHALL be the only task that pushes delivery changes to the remote
- **AND** the workflow SHALL NOT declare a separate `integrate:push` task or any other remote-writing task
