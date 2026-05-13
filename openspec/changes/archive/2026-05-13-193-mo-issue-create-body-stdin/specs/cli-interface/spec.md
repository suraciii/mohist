## MODIFIED Requirements

### Requirement: Issue CLI accepts long body input without shell-sensitive escaping

`mo issue create` and `mo issue update` SHALL accept issue body input as a literal string, as a curl-style `@file` reference, and as `-` to read the full body from stdin before sending the request to the API.

#### Scenario: Create issue from body file reference
- **WHEN** the user runs `mo issue create "Title" --body @body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the issue body

#### Scenario: Create issue from explicit body file option
- **WHEN** the user runs `mo issue create "Title" --body-file body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the issue body

#### Scenario: Create issue from stdin
- **WHEN** the user pipes content into `mo issue create "Title" --body -`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the issue body

#### Scenario: Update issue from body file reference
- **WHEN** the user runs `mo issue update 42 --body @body.md`
- **THEN** the CLI reads `body.md` as UTF-8 text
- **AND** sends the file contents as the updated issue body

#### Scenario: Update issue from stdin
- **WHEN** the user pipes content into `mo issue update 42 --body -`
- **THEN** the CLI reads the full stdin stream
- **AND** sends the streamed text as the updated issue body

#### Scenario: Preserve literal body behavior
- **WHEN** the user runs `mo issue create "Title" --body "literal markdown body"`
- **THEN** the CLI sends the provided string unchanged as the issue body

### Requirement: Issue CLI normalizes touched priority inputs and fails invalid inputs with exit code 1

The touched issue CLI flows SHALL normalize priority inputs case-insensitively for create, update, and list, and SHALL terminate with exit code `1` when touched argument validation or body-ingestion fails.

#### Scenario: Create accepts uppercase priority
- **WHEN** the user runs `mo issue create "Title" -p P2`
- **THEN** the CLI accepts the value
- **AND** sends normalized priority `p2`

#### Scenario: Update accepts uppercase priority
- **WHEN** the user runs `mo issue update 42 -p P0`
- **THEN** the CLI accepts the value
- **AND** sends normalized priority `p0`

#### Scenario: List accepts uppercase priority filter
- **WHEN** the user runs `mo issue list -p P1`
- **THEN** the CLI accepts the value
- **AND** applies the same filter as `-p p1`

#### Scenario: Invalid priority fails non-zero
- **WHEN** the user runs `mo issue create "Title" -p urgent`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code `1`

#### Scenario: Missing body file fails non-zero
- **WHEN** the user runs `mo issue create "Title" --body @missing.md`
- **THEN** the CLI prints a clear file-read error
- **AND** exits with code `1`

#### Scenario: Conflicting body sources fail non-zero
- **WHEN** the user runs `mo issue create "Title" --body @a.md --body-file b.md`
- **THEN** the CLI prints a clear validation error about conflicting body sources
- **AND** exits with code `1`

### Requirement: Issue create success output guides the next step only for startable issues

Successful `mo issue create` output SHALL print the created issue number and priority, and SHALL show the `mo issue start` hint only when the created issue is still in a startable draft or backlog state.

#### Scenario: Start tip shown for backlog issue
- **WHEN** `mo issue create` returns an issue still in a startable draft or backlog state
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** prints `Tip: Run 'mo issue start <number>' to begin processing`

#### Scenario: Start tip omitted for non-startable issue
- **WHEN** `mo issue create` returns an issue already outside the initial startable state
- **THEN** the CLI prints `Created issue #N: <title>`
- **AND** prints the issue priority
- **AND** does not print the start tip
