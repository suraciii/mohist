## ADDED Requirements

### Requirement: Issue repository references resolve from current project configuration
Issue repository resolution SHALL treat project repository configuration as the only authority for repository identity, path, remote, base branch, and default selection. An issue SHALL carry only a stable repository reference within its project, and every runtime or read-time repository lookup SHALL resolve that reference against the current project repositories.

#### Scenario: Resolve explicit issue repository reference
- **WHEN** an issue stores a repository reference that matches one project repository
- **THEN** Mohist SHALL resolve repository id or name, path, remote, base branch, and default metadata from the current project repository configuration
- **AND** it SHALL NOT use issue-stored snapshot fields as authoritative repository configuration

#### Scenario: Resolve default repository reference at issue creation time
- **WHEN** an issue is created without an explicit repository selection
- **THEN** Mohist SHALL bind the issue to the current default project repository reference
- **AND** subsequent repository reads SHALL resolve that reference from the current project configuration

#### Scenario: Project repository configuration changes after issue creation
- **WHEN** a project's repository path, remote, base branch, or default marker changes after an issue is created
- **THEN** later issue reads and workflow repository lookups SHALL use the newly resolved project repository configuration
- **AND** stale issue snapshot values SHALL NOT override the project-owned repository configuration

#### Scenario: Referenced repository no longer exists
- **WHEN** an issue references a repository that is no longer present in the project configuration
- **THEN** Mohist SHALL surface a repository configuration error that identifies the missing reference
- **AND** it SHALL NOT silently fall back to another repository or an implicit `main` branch

#### Scenario: Repository reference resolves ambiguously
- **WHEN** an issue repository reference matches more than one project repository candidate
- **THEN** Mohist SHALL surface a repository configuration error describing the ambiguity
- **AND** it SHALL require project configuration repair before repository-dependent issue operations proceed
