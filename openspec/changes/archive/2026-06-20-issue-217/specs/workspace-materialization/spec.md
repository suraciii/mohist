## REMOVED Requirements

### Requirement: mohist/prepare operates inside the bound workflow workspace

**Reason:** The `mohist/prepare` action is deleted. Its remote-fetch rebase and conflict-resolution behavior is absorbed into the unified `mohist/rebase` action (which also performs the squash), so the prepare-specific contract no longer exists.

**Migration:** The bound-workspace, no-materialization contract now applies to `mohist/rebase`, covered by the ADDED "mohist/rebase operates inside the bound workflow workspace" requirement. `integrate:rebase` uses that action with `remote=origin` and `squash=true`.

## ADDED Requirements

### Requirement: mohist/rebase operates inside the bound workflow workspace

`mohist/rebase` SHALL run its fetch, rebase, conflict resolution, and optional squash against the existing bound workflow workspace on its `workspace.branch`. It SHALL NOT trigger workflow workspace creation, repository cloning, or workflow-start materialization. A run that has not yet bound a workflow workspace SHALL fail rebase as a workspace-missing infrastructure failure rather than materialize a workspace on demand. The unified `mohist/rebase` action SHALL serve both the manual `POST /{number}/rebase` endpoint and the Integrate `integrate:rebase` task, differing only by input parameters (for example `remote` and `squash`).

#### Scenario: Rebase uses the bound workspace without materializing

- **WHEN** `integrate:rebase` runs for a run that owns a bound workflow workspace
- **THEN** `mohist/rebase` SHALL fetch the remote base ref and rebase inside the bound workflow workspace
- **AND** it SHALL NOT run `git clone` to create or recover the workflow workspace
- **AND** it SHALL leave the workflow workspace on its `workspace.branch`

#### Scenario: Rebase without a bound workspace fails as infrastructure

- **WHEN** `mohist/rebase` runs for a run whose workflow workspace is missing or unbound
- **THEN** rebase SHALL fail with a workspace-missing infrastructure failure
- **AND** it SHALL NOT materialize a workflow workspace as a side effect of rebase

#### Scenario: Manual and integrate rebase share one action

- **WHEN** the manual `POST /{number}/rebase` endpoint and the `integrate:rebase` task each invoke rebase
- **THEN** both SHALL resolve to the same unified `mohist/rebase` action
- **AND** they SHALL differ only by their input parameters, not by separate rebase implementations
