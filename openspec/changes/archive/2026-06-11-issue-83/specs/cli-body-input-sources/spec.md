## ADDED Requirements

### Requirement: `issue create` and `issue update` accept three mutually exclusive body input sources

`mo issue create` and `mo issue update` SHALL accept the issue body from exactly one of three mutually exclusive sources: `--body <text>` (literal string), `--body-file <path>` (read a UTF-8 text file), or `--body-stdin` (read the full stdin stream). Exactly one source SHALL be required. Passing zero or more than one source SHALL cause the CLI to print a clear validation error and exit with a non-zero status.

#### Scenario: Inline `--body` is the literal body
- **WHEN** the user runs `mo issue create "Title" --body "literal markdown body"`
- **THEN** the CLI sends the provided string unchanged as the issue body

#### Scenario: `--body-file` reads a UTF-8 file
- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **AND** `body.md` exists and is readable as UTF-8
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the issue body

#### Scenario: `--body-stdin` reads the full stdin stream
- **WHEN** the user pipes content into `mo issue create "Title" --body-stdin`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the issue body

#### Scenario: Update from body file
- **WHEN** the user runs `mo issue update 42 --body-file body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the updated issue body

#### Scenario: Update from stdin
- **WHEN** the user pipes content into `mo issue update 42 --body-stdin`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the updated issue body

#### Scenario: Missing body source fails with a clear error
- **WHEN** the user runs `mo issue create "Title"` without any body option
- **THEN** the CLI prints a clear validation error that the body is required
- **AND** exits with a non-zero status
- **AND** does not make a server request

#### Scenario: Two body sources fail with a clear error
- **WHEN** the user runs `mo issue create "Title" --body "inline" --body-file body.md`
- **THEN** the CLI prints a clear validation error that `--body` and `--body-file` are mutually exclusive
- **AND** exits with a non-zero status
- **AND** does not make a server request

#### Scenario: All three body sources fail with a clear error
- **WHEN** the user runs `mo issue create "Title" --body "a" --body-file b.md --body-stdin` and pipes stdin
- **THEN** the CLI prints a clear validation error mentioning the conflict
- **AND** exits with a non-zero status
- **AND** does not make a server request

### Requirement: File-read failures fail with a clear error and non-zero exit

When `--body-file <path>` is passed but the file does not exist, is not readable, or fails to read as UTF-8, the CLI SHALL print a clear file-read error, exit with code `1`, and SHALL NOT make a server request.

#### Scenario: Missing file fails non-zero
- **WHEN** the user runs `mo issue create "Title" --body-file missing.md`
- **AND** `missing.md` does not exist
- **THEN** the CLI prints a clear file-read error
- **AND** exits with status `1`
- **AND** does not make a server request

#### Scenario: Unreadable file fails non-zero
- **WHEN** the user runs `mo issue create "Title" --body-file locked.md`
- **AND** the file cannot be read for any reason
- **THEN** the CLI prints a clear file-read error
- **AND** exits with status `1`
- **AND** does not make a server request

### Requirement: Body input source resolution is applied before the create/update request

The body input source resolution (literal string, file read, or stdin drain) SHALL happen before the create or update request is constructed. The resolved body text SHALL be sent in the request body. The CLI SHALL NOT echo or log the resolved body text in normal (non-debug) operation.

#### Scenario: Resolved body is sent to the server
- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **THEN** the CLI resolves `body.md` to text
- **AND** sends the resolved text in the `POST /api/issues` request body
- **AND** the server stores the same text the file contained

#### Scenario: Stdin body is drained before the request
- **WHEN** the user runs `mo issue create "Title" --body-stdin` and pipes content
- **THEN** the CLI reads the full stdin stream to completion
- **AND** sends the drained text in the `POST /api/issues` request body
- **AND** does not leave stdin partially consumed

#### Scenario: Source resolution is independent of other create options
- **WHEN** the user runs `mo issue create "Title" --body-file body.md --model anthropic/claude-sonnet`
- **THEN** the CLI resolves the body source and the model
- **AND** the create request includes both the resolved body text and the model

### Requirement: `--body-stdin` and `--body-file` are documented in help text

`mo issue create --help` and `mo issue update --help` SHALL document `--body`, `--body-file <path>`, and `--body-stdin` with a one-line description of each. Help text SHALL note the mutual-exclusion rule. `--body-file` SHALL be the recommended option for long Markdown bodies.

#### Scenario: Create help lists all three body options
- **WHEN** the user runs `mo issue create --help`
- **THEN** the output SHALL list `--body <body>`, `--body-file <path>`, and `--body-stdin`
- **AND** SHALL note that they are mutually exclusive
- **AND** SHALL recommend `--body-file` for long Markdown bodies

#### Scenario: Update help lists all three body options
- **WHEN** the user runs `mo issue update --help`
- **THEN** the output SHALL list `--body <body>`, `--body-file <path>`, and `--body-stdin`
- **AND** SHALL note that they are mutually exclusive
