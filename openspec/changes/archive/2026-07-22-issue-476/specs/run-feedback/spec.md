### Requirement: Approval feedback reads live under `mo run feedback`

`mo run feedback list` and `mo run feedback view` SHALL be the only CLI paths for reading approval feedback. Feedback SHALL NOT appear as a subcommand of `mo issue` or `mo session`. Both commands SHALL accept a positional Run ID or `--issue <number>` as target, following the same mutual-exclusion contract as other `mo run` verbs.

#### Scenario: Feedback list under run resolves

- **WHEN** the user runs `mo run feedback list wr_abc123`
- **THEN** the CLI SHALL retrieve approval feedback records for the targeted Run

#### Scenario: Feedback list by issue selector

- **WHEN** the user runs `mo run feedback list --issue 42` and issue 42 is bound to `wr_abc123`
- **THEN** the CLI SHALL resolve `wr_abc123` from the issue
- **AND** SHALL retrieve feedback records for that Run

#### Scenario: Removed issue feedback path does not resolve

- **WHEN** the user runs `mo issue feedback list 42`
- **THEN** the command SHALL fail to resolve and SHALL exit non-zero
- **AND** no HTTP request SHALL be issued

### Requirement: `feedback list` returns a collection with optional stage filter

`mo run feedback list` SHALL return a collection of feedback records for the targeted Run. It SHALL accept `--stage <stage>` to filter by workflow stage. It SHALL support `--json <fields>` for field selection. An empty result SHALL exit 0.

#### Scenario: List feedback for a run

- **WHEN** the user runs `mo run feedback list wr_abc123`
- **THEN** the CLI SHALL retrieve feedback records for `wr_abc123`
- **AND** each record SHALL include id, stage, body, and timestamps

#### Scenario: List feedback filtered by stage

- **WHEN** the user runs `mo run feedback list wr_abc123 --stage plan`
- **THEN** the CLI SHALL retrieve only feedback records whose stage is `plan`

#### Scenario: List feedback with no records exits zero

- **WHEN** the user runs `mo run feedback list wr_abc123` and the run has no feedback records
- **THEN** the CLI SHALL exit 0

### Requirement: `feedback view` reads a single feedback record

`mo run feedback view` SHALL display a single feedback record. It SHALL require either `--feedback <id>` to read a specific record or `--latest` to read the most recent record (optionally filtered by `--stage`). Providing neither selector SHALL fail with exit code 1 and SHALL NOT issue an HTTP request. It SHALL support `--json <fields>` for field selection.

#### Scenario: View a specific feedback record

- **WHEN** the user runs `mo run feedback view wr_abc123 --feedback fb_001`
- **THEN** the CLI SHALL retrieve and display feedback record `fb_001`

#### Scenario: View the latest feedback record

- **WHEN** the user runs `mo run feedback view wr_abc123 --latest`
- **THEN** the CLI SHALL retrieve the most recent feedback record for the Run

#### Scenario: View latest feedback filtered by stage

- **WHEN** the user runs `mo run feedback view wr_abc123 --latest --stage check`
- **THEN** the CLI SHALL retrieve the most recent feedback record whose stage is `check`

#### Scenario: View without a selector fails locally

- **WHEN** the user runs `mo run feedback view wr_abc123`
- **THEN** the CLI SHALL exit 1
- **AND** stderr SHALL state that `--feedback <id>` or `--latest` is required
- **AND** no HTTP request SHALL be issued

### Requirement: Feedback target resolution fails clearly on missing run

When `--issue <number>` is used and the issue has no bound WorkflowRun, the feedback commands SHALL fail with a non-zero exit and a diagnostic that names the issue and states it has no active run.

#### Scenario: Feedback list on issue without a run reports the missing binding

- **WHEN** the user runs `mo run feedback list --issue 99` and issue 99 has no `workflowRunId`
- **THEN** the CLI SHALL exit non-zero
- **AND** stderr SHALL state that issue 99 has no active workflow run
