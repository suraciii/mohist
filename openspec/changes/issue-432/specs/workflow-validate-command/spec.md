### Requirement: Local-only validation command

The CLI MUST provide `mo workflow validate --file <path>` to validate a local Workflow Definition. `--file -` MUST read the Definition from standard input. The command MUST NOT connect to a Server and MUST NOT parse a Project.

#### Scenario: validate from a file
- **WHEN** a user runs `mo workflow validate --file ./workflow.yaml`
- **THEN** the command reads the file and validates its contents without contacting any Server or resolving a Project

#### Scenario: validate from stdin
- **WHEN** a user pipes a Definition into `mo workflow validate --file -`
- **THEN** the command reads the Definition from standard input and validates it without contacting any Server or resolving a Project

### Requirement: Valid input succeeds clearly

For a valid Definition, the command MUST report success and exit with code zero.

#### Scenario: valid definition
- **WHEN** the supplied Definition satisfies every Definition-language rule
- **THEN** the command reports that the Definition is valid and exits with code zero

### Requirement: Invalid input reports Definition errors and exits non-zero

For an invalid Definition, the command MUST report the same Definition errors the Profile save path would produce and MUST exit with a non-zero code.

#### Scenario: invalid definition
- **WHEN** the supplied Definition contains an unknown field and a type error
- **THEN** the command reports both errors using the same YAML paths and messages as the save path and exits with a non-zero code

### Requirement: Local command judges only the Definition language

The command MUST report only Definition-language errors. It MUST NOT claim to validate whether an action `uses` exists or whether a `with` value satisfies an Action contract, because those judgments require the Action catalog and are not available to a local, offline command.

#### Scenario: action existence is not asserted
- **WHEN** a Definition references an action that is not installed locally
- **THEN** the command does not report an error about the action's existence and does not claim the action is valid
