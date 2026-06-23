### Requirement: CLI provides mo issue feedback create command

The CLI SHALL provide `mo issue feedback create <issue-number> --stage <stage> --body <text>` to create an approval feedback record via `POST /api/issues/:number/feedback`. The command SHALL accept `--stage` and `--body` as required flags. The `--body` flag SHALL accept a literal string and `--body-file` as a file reference for long feedback, consistent with `mo issue create --body/--body-file` behavior. The command SHALL accept `--project/--project-id` and `-o table|json`. A successfully created feedback record SHALL be queryable via `mo issue feedback list <issue-number>`.

#### Scenario: Create feedback with stage and body

- **WHEN** the user runs `mo issue feedback create 42 --stage plan --body "Rethink the data model"`
- **THEN** the CLI sends `POST /api/issues/42/feedback` with `stage: "plan"` and `body: "Rethink the data model"`
- **AND** prints the created feedback identifier on success
- **AND** `mo issue feedback list 42` returns a list containing the new record

#### Scenario: Create feedback from file

- **WHEN** the user runs `mo issue feedback create 42 --stage build --body-file feedback.md`
- **THEN** the CLI reads `feedback.md` as UTF-8 text
- **AND** sends the file contents as the feedback body with `stage: "build"`

#### Scenario: Missing stage fails clearly

- **WHEN** the user runs `mo issue feedback create 42 --body "Some feedback"`
- **THEN** the CLI prints a clear validation error indicating `--stage` is required
- **AND** exits with code 1

#### Scenario: Missing body fails clearly

- **WHEN** the user runs `mo issue feedback create 42 --stage plan`
- **THEN** the CLI prints a clear validation error indicating `--body` or `--body-file` is required
- **AND** exits with code 1

#### Scenario: Feedback create appears in help

- **WHEN** the user runs `mo issue feedback --help`
- **THEN** the output lists `create` alongside the existing `list` and `show` subcommands
