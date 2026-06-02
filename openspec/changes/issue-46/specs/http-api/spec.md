## MODIFIED Requirements

### Requirement: REQ-API-198-001 Issue create accepts model with existing priority support

`POST /api/issues` SHALL accept optional `model` and `priority` fields in the same request body as title, body, and labels. It SHALL bind the issue to the selected project repository reference when `repositoryName` or equivalent repository identity is provided, or to the current default project repository reference when no repository is provided. The response SHALL expose repository details resolved from the current project configuration rather than an issue-owned repository snapshot.

#### Scenario: Create issue with model and priority
- **WHEN** the server receives `POST /api/issues` with `{ title, body?, labels?, priority: "p1", model: "anthropic/claude-sonnet" }`
- **THEN** it creates the issue with both values persisted
- **AND** returns the created issue including `priority` and `model`

#### Scenario: Create issue with invalid model format
- **WHEN** the server receives `POST /api/issues` with `model: "invalid-model"`
- **THEN** it returns 400
- **AND** the error explains that `provider/model` format is required

#### Scenario: Create issue with explicit repository selection stores only reference
- **WHEN** the server receives `POST /api/issues` with a repository identity that matches one project repository
- **THEN** it SHALL persist only the issue repository reference
- **AND** the response SHALL include repository details resolved from the current project repository configuration

#### Scenario: Create issue without repository selection binds to current default repository
- **WHEN** the server receives `POST /api/issues` without a repository identity
- **THEN** it SHALL bind the issue to the current default project repository reference
- **AND** the response SHALL include that resolved repository context

### Requirement: Review APIs expose merge-base comparison data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads for the issue's pending merge content. Issue-level review data SHALL be framed around the current merge relationship between base and head, and issue-level diff data SHALL represent the merge-base-to-head change set rather than a generic two-dot base-vs-head comparison. The base branch, repository path, and related repository context SHALL be resolved from the issue's current project repository reference for each request.

#### Scenario: Diff API returns merge-base comparison

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison`
- **AND** `comparison` is `merge-base`
- **AND** the summary, file list, and per-file patch content are equivalent to `git diff <base>...<head>`

#### Scenario: Behind-base branch excludes base-only changes

- **WHEN** the issue branch is behind the base branch
- **THEN** `GET /api/issues/:number/diff` does not report files changed only on base
- **AND** the returned file count matches the issue's pending merge contribution from the merge base

#### Scenario: Commits API shares comparison metadata

- **WHEN** `GET /api/issues/:number/commits` is called for an issue with an existing worktree and accessible base/head branches
- **THEN** the response includes the same `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison` metadata as the diff response
- **AND** returns the complete commit range that is reachable from head and not from base
- **AND** its summary counts are consistent with the issue-level diff response

#### Scenario: Commit diff remains commit-scoped

- **WHEN** `GET /api/issues/:number/commits/:hash/diff` is called for a commit that belongs to the issue branch
- **THEN** the response remains a single-commit diff payload
- **AND** it does not redefine the default issue-level Files changed semantic away from merge-base comparison

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the worktree is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `worktree_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI

#### Scenario: Review API reflects current project repository configuration after issue creation
- **WHEN** the project's repository path or base branch changes after the issue was created
- **THEN** the review APIs SHALL use the resolved current project repository configuration for that issue repository reference
- **AND** they SHALL NOT use stale repository path or base branch values copied from the issue row

#### Scenario: Review API reports missing repository reference clearly
- **WHEN** the issue repository reference cannot be resolved in the current project configuration
- **THEN** the API SHALL return a clear repository configuration error response
- **AND** it SHALL NOT silently fall back to another repository or implicit branch defaults

### Requirement: API 提供操作接口

`POST /api/issues/:number/rebase` SHALL schedule visible workflow rebase work for non-Done stages through the active WorkflowRun instead of enqueueing a hidden issue task queue `rebase` job. The response SHALL communicate that workflow work was scheduled, and the current stage task list SHALL become the canonical source of progress. Repository path and base branch for rebase SHALL be resolved from the issue's current project repository reference.

#### Scenario: Non-Done rebase schedules WorkflowRun task

- **WHEN** a client calls `POST /api/issues/:number/rebase` for an issue in Plan, Build, Check, or Integrate
- **THEN** the API SHALL append or reuse `rebase-branch` in the current WorkflowRun stage
- **AND** it SHALL NOT use the hidden issue task queue `rebase` job as the primary execution path
- **AND** the response SHALL indicate that rebase work is now represented in workflow task state

#### Scenario: Duplicate rebase request is idempotent for in-flight work

- **WHEN** a client calls `POST /api/issues/:number/rebase`
- **AND** the current stage already has a `rebase-branch` task in `pending` or `running` state
- **THEN** the API SHALL return success without scheduling a duplicate task
- **AND** the existing workflow task SHALL remain the canonical progress record

#### Scenario: Rebase uses resolved current repository base branch
- **WHEN** the project's repository base branch changes after the issue was created
- **THEN** `POST /api/issues/:number/rebase` SHALL target the resolved current project repository base branch for that issue repository reference
- **AND** it SHALL NOT use a stale issue-owned repository snapshot base branch
