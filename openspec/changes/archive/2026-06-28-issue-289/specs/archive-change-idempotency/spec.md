## MODIFIED Requirements

### Requirement: Idempotent archive directory naming across retries

The `mohist/archive-change` action SHALL make its directory move idempotent across retries and reruns. Before moving the change directory into the archive, the action SHALL persist the computed archive directory name to the single run-scoped workflow runtime variable **`openspecArchiveName`**, whose value is the archive directory basename (for example `2026-06-27-issue-276`) — not an absolute path and not a map keyed by change directory. On any retry or rerun, the action SHALL read `openspecArchiveName` and reuse the same archive directory name, locating the archive at `${dirname(changeDir)}/archive/${openspecArchiveName}`, so that the already-archived directory is located instead of computing a fresh name. The action SHALL NOT derive the archive directory name from the current wall-clock date alone across a retry boundary, because a cross-day retry would otherwise compute a different prefix and fail with `missing-source` once the source directory has already been moved. This requirement SHALL apply to every workflow profile that uses `mohist/archive-change` (both `mohist/github-pr` and `mohist/default`).

#### Scenario: Archive name persisted before the move

- **WHEN** `mohist/archive-change` computes the archive directory name for the first time
- **THEN** the action SHALL persist that name to the `openspecArchiveName` run-scoped variable as the archive directory basename before moving the source change directory
- **AND** the directory move SHALL occur only after `openspecArchiveName` has been persisted

#### Scenario: Retry reuses persisted archive name

- **WHEN** `mohist/archive-change` is retried after a prior execution persisted `openspecArchiveName`
- **THEN** the action SHALL read the archive directory name from `openspecArchiveName`
- **AND** SHALL reuse that exact basename rather than recomputing it from the current date
- **AND** SHALL locate the archive at `${dirname(changeDir)}/archive/${openspecArchiveName}`

#### Scenario: Cross-day retry finds the archived directory

- **WHEN** a first execution moved the change directory into the archive on day N and persisted `openspecArchiveName`, and a retry or rerun executes on day N+1
- **THEN** the action SHALL reuse the basename persisted on day N from `openspecArchiveName`
- **AND** SHALL locate the already-archived directory
- **AND** SHALL NOT fail with `missing-source`

#### Scenario: Persist failure before move is retry-safe

- **WHEN** persisting `openspecArchiveName` fails before the directory move is attempted
- **THEN** the action SHALL NOT move the source change directory
- **AND** SHALL return a retry-safe failure tagged with the `persist-name` stage
- **AND** a retry of the same task SHALL be able to re-attempt the persist

#### Scenario: Applies to all profiles using archive-change

- **WHEN** either the `mohist/github-pr` or `mohist/default` profile archives a change
- **THEN** the `mohist/archive-change` action SHALL exhibit the same idempotent archive-naming behavior using `openspecArchiveName`

## ADDED Requirements

### Requirement: Backfill archive name from existing archive on missing source

When `mohist/archive-change` is retried and the source change directory is gone, no `openspecArchiveName` variable is present, but an existing archive directory matching the current archive prefix is found, the action SHALL backfill `openspecArchiveName` from that existing archive's basename before continuing. This closes the gap where a mid-flight persist was missing and prevents any subsequent same-run cross-date retry from recomputing the archive prefix from the wall-clock date. The action SHALL persist `openspecArchiveName` before treating the existing archive as the archive target, and a persist failure during this backfill continuation SHALL return a retry-safe failure tagged with the `persist-name` stage.

#### Scenario: Existing archive backfills the archive name

- **WHEN** the source change directory does not exist
- **AND** `openspecArchiveName` is not present in the run variables
- **AND** an existing archive directory matching the current archive prefix is found
- **THEN** the action SHALL set `openspecArchiveName` to the basename of the existing archive directory
- **AND** SHALL persist `openspecArchiveName` before continuing
- **AND** SHALL NOT fail with `missing-source`

#### Scenario: Subsequent same-run retry reuses the backfilled name

- **WHEN** a prior backfill in the same run set `openspecArchiveName` from an existing archive and a later cross-date retry occurs
- **THEN** the action SHALL read the backfilled `openspecArchiveName`
- **AND** SHALL locate the archive at `${dirname(changeDir)}/archive/${openspecArchiveName}`
- **AND** SHALL NOT recompute the archive prefix from the current date

#### Scenario: Backfill persist failure is retry-safe

- **WHEN** the action has identified an existing archive to backfill `openspecArchiveName` from and the subsequent persist fails
- **THEN** the action SHALL return a retry-safe failure tagged with the `persist-name` stage
- **AND** a retry of the same task SHALL be able to re-attempt the backfill persist

### Requirement: Legacy archive-name variable compatibility and migration

For workflow runs that started before `openspecArchiveName` existed, the `mohist/archive-change` action SHALL continue to read the legacy nested-map run variable `_actions.archiveChange.destination[changeRel]`, where `changeRel` is the change directory's path relative to the run workspace. Reading the legacy key SHALL be best-effort: when both keys are present, `openspecArchiveName` SHALL take precedence. When only the legacy key is present, the action SHALL best-effort migrate it by writing `openspecArchiveName` to the basename that the legacy entry resolves to, so that subsequent retries in the same run use the new key. The legacy key SHALL be read-only; the action SHALL NOT write `_actions.archiveChange.destination` for any new run.

#### Scenario: Legacy key read for in-flight runs

- **WHEN** a workflow run started before `openspecArchiveName` was introduced and only `_actions.archiveChange.destination[changeRel]` is present
- **THEN** the action SHALL read the archive directory basename from the legacy nested-map entry
- **AND** SHALL reuse it as the archive directory name for the current execution

#### Scenario: Legacy key migrated to openspecArchiveName

- **WHEN** only the legacy `_actions.archiveChange.destination[changeRel]` key is present
- **THEN** the action SHALL best-effort write `openspecArchiveName` to the basename the legacy entry resolves to
- **AND** subsequent retries in the same run SHALL use `openspecArchiveName` instead of the legacy key

#### Scenario: Both keys present prefer openspecArchiveName

- **WHEN** both `openspecArchiveName` and `_actions.archiveChange.destination[changeRel]` are present
- **THEN** the action SHALL prefer the value from `openspecArchiveName`
- **AND** SHALL ignore the legacy entry for archive-name resolution
