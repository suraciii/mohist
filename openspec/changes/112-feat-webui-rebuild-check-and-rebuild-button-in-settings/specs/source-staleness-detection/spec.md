## ADDED Requirements

### Requirement: Status API includes source HEAD comparison

The system SHALL extend `GET /api/status` to include `sourceHead` (the current git HEAD commit hash of the source repository) and `upToDate` (boolean indicating whether `sourceHead` equals the build-time `gitHash`).

#### Scenario: Source mode with matching commits

- **WHEN** client requests `GET /api/status`
- **AND** server is running in source mode
- **AND** current `git rev-parse HEAD` in the source repository equals the build-time `gitHash`
- **THEN** response includes `sourceHead` with the current HEAD hash
- **AND** `upToDate` is `true`

#### Scenario: Source mode with differing commits

- **WHEN** client requests `GET /api/status`
- **AND** server is running in source mode
- **AND** current `git rev-parse HEAD` differs from the build-time `gitHash`
- **THEN** response includes `sourceHead` with the current HEAD hash
- **AND** `upToDate` is `false`

#### Scenario: Non-source mode fallback

- **WHEN** client requests `GET /api/status`
- **AND** server is NOT running in source mode (`detectInstallMode().workingDir` is undefined)
- **THEN** `sourceHead` is `null`
- **AND** `upToDate` is `true` (no source comparison possible, assume up to date)

#### Scenario: Git command fails gracefully

- **WHEN** client requests `GET /api/status`
- **AND** `git rev-parse HEAD` fails (e.g. not a git repo, git not installed)
- **THEN** `sourceHead` is `null`
- **AND** `upToDate` is `true`
- **AND** no error is thrown
