## MODIFIED Requirements

### Requirement: API 提供项目管理接口

Server SHALL provide RESTful project management APIs. API handlers SHALL operate through ProjectService and SHALL NOT accept, persist, or return project local filesystem paths, effective paths, checkout paths, or equivalent local execution directories.

#### Scenario: 列出项目
- **WHEN** CLI 请求 `GET /api/projects`
- **THEN** 返回所有已注册的项目列表
- **AND** each project response SHALL NOT include `path`, `effectivePath`, or local checkout fields

#### Scenario: 创建项目
- **WHEN** CLI 请求 `POST /api/projects` with `{ name, repository: { name, gitUrl, baseBranch } }`
- **THEN** 通过 ProjectService 创建新项目
- **AND** the API SHALL create the initial repository in the same operation and mark it as the default
- **AND** the API SHALL NOT require `path`
- **AND** the returned project SHALL NOT include `path`, `effectivePath`, or local checkout fields

#### Scenario: 删除项目
- **WHEN** CLI 请求 `DELETE /api/projects/:name`
- **THEN** 通过 ProjectService 从项目列表中移除项目

#### Scenario: 切换当前项目
- **WHEN** CLI 请求 `POST /api/projects/:name/use`
- **THEN** 通过 ProjectService 设置当前项目

### Requirement: Review APIs expose merge-base comparison data

`GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, and `GET /api/issues/:number/commits/:hash/diff` SHALL return availability-aware review payloads for the issue's pending merge content from the runner-managed workflow workspace. Issue-level review data SHALL be framed around the current merge relationship between base and head in the workflow workspace, and issue-level diff data SHALL represent the merge-base-to-head change set rather than a generic two-dot base-vs-head comparison. These APIs SHALL NOT assume a project path, repository path, or `mo/issue-N` git worktree under a user checkout.

#### Scenario: Diff API returns merge-base comparison

- **WHEN** `GET /api/issues/:number/diff` is called for an issue with an existing workflow workspace and accessible base/head refs
- **THEN** the response includes `available: true` and `reason: null`
- **AND** includes `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison`
- **AND** `comparison` is `merge-base`
- **AND** the summary, file list, and per-file patch content are equivalent to `git diff <base>...<head>` executed inside the workflow workspace

#### Scenario: Behind-base branch excludes base-only changes

- **WHEN** the issue branch is behind the base branch in the workflow workspace
- **THEN** `GET /api/issues/:number/diff` does not report files changed only on base
- **AND** the returned file count matches the issue's pending merge contribution from the merge base

#### Scenario: Commits API shares comparison metadata

- **WHEN** `GET /api/issues/:number/commits` is called for an issue with an existing workflow workspace and accessible base/head refs
- **THEN** the response includes the same `base`, `head`, `mergeBase`, `ahead`, `behind`, `canFastForward`, and `comparison` metadata as the diff response
- **AND** returns the complete commit range that is reachable from head and not from base
- **AND** its summary counts are consistent with the issue-level diff response

#### Scenario: Commit diff remains commit-scoped

- **WHEN** `GET /api/issues/:number/commits/:hash/diff` is called for a commit that belongs to the issue branch in the workflow workspace
- **THEN** the response remains a single-commit diff payload
- **AND** it does not redefine the default issue-level Files changed semantic away from merge-base comparison

#### Scenario: Review data unavailable by lifecycle

- **WHEN** review data is unavailable because the workflow workspace is removed, the issue has not started, a branch is missing, or git fails
- **THEN** the response data includes `available: false`
- **AND** `reason` is one of `workspace_removed`, `not_started`, `branch_missing`, or `git_error`
- **AND** `message` explains the cause for display in the UI

## ADDED Requirements

### Requirement: Repository APIs require Git URL
Repository creation SHALL require `gitUrl`; repository metadata updates SHALL accept one or both of `gitUrl` and `baseBranch`. Creation requests SHALL use `setDefault: true` only when selecting the newly added repository. Repository responses SHALL expose the server-derived `isDefault` value, and clients SHALL NOT provide `isDefault`. Repository request and response contracts SHALL NOT accept or return `path`, `remote`, `resolvedPath`, or equivalent local checkout fields.

#### Scenario: Create repository with Git URL
- **WHEN** a client requests repository creation with `{ name, gitUrl, baseBranch, setDefault? }`
- **THEN** the API SHALL create the repository reference
- **AND** the response SHALL include `gitUrl` and `baseBranch`
- **AND** the response SHALL NOT include local path fields

#### Scenario: Reject an invalid default-selection control
- **WHEN** a client supplies `isDefault`, `setDefault: false`, or `setDefault: null` in a repository mutation request
- **THEN** the API SHALL return a 400-class validation error
- **AND** the repository declaration and default selection SHALL remain unchanged

#### Scenario: Reject path-only repository request
- **WHEN** a client requests repository creation or update with `path` and without `gitUrl`
- **THEN** the API SHALL return a 400-class validation error
- **AND** the repository SHALL NOT be created or updated

### Requirement: Issue start contract exposes workspace semantics
Issue start and issue detail APIs SHALL expose repository metadata and workflow workspace status without exposing project or repository local execution paths. Start dispatch data SHALL include `repository.gitUrl`, `repository.baseBranch`, and `workspace.path` as the only local execution directory.

#### Scenario: Start issue dispatch variables omit local repository paths
- **WHEN** a client starts an issue through `POST /api/issues/:number/start`
- **THEN** the workflow dispatch variables SHALL include repository Git URL metadata and `workspace.path`
- **AND** the variables SHALL NOT include `project.path`, `project.effectivePath`, `repository.path`, `repository.remote`, or `repository.resolvedPath`

#### Scenario: Workspace APIs use workspace language
- **WHEN** a client reads status, cleanup, diff, commits, or file content for workflow execution data
- **THEN** API names and response fields SHALL use workspace terminology
- **AND** user-facing responses SHALL NOT require clients to know `worktree` paths or `mo/issue-N` branch naming assumptions
