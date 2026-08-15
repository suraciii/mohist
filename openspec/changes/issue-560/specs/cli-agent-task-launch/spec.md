### Requirement: One command creates and launches from a task

The CLI SHALL provide `mo agent start` as the task-first startup command: it
creates an Agent for the task and launches it in one step, and it takes no
Agent argument. It SHALL accept the task as `--prompt` or `--prompt-file`
(mutually exclusive), optional `--attach` attachments, the optional identity
hint `--name`, the execution hints `--runtime`, `--model`, and `--variant`,
the same context flags as `mo agent launch` (`--issue`, `--epic`, `--repo`,
`--workspace`), `--project`, and the standard output selection. Flag
validation SHALL mirror `mo agent launch` and `mo agent create`: `--runtime`
accepts only `opencode` or `pi`, and set/clear conflicts are rejected as
usage failures.

#### Scenario: A task alone starts an Agent

- **WHEN** the user runs `mo agent start --prompt "<task>"` in a Project whose default execution configuration resolves the execution configuration
- **THEN** the command creates one Agent for the task, launches it in the same step, and prints the resulting identities

#### Scenario: Hints compose with the Project default

- **WHEN** the user adds `--runtime pi --model provider/model`, or any subset of the execution hints
- **THEN** the command sends those hints and the Server resolves every unspecified field under the precedence rule

### Requirement: The idempotency-key contract matches `mo agent launch`

`mo agent start` SHALL follow the same caller-visible idempotency-key contract
as `mo agent launch`: `--idempotency-key` may be supplied; when omitted, the
command generates a key and prints it before the request in table mode; after
a lost response the caller retries with that key and receives the original
outcome. A retry MUST NOT create a second Agent or launch.

#### Scenario: The generated key is printed before the request

- **WHEN** `mo agent start` runs without `--idempotency-key` in table mode
- **THEN** the command prints the generated key before the launch response

#### Scenario: A retry with the printed key returns the original launch

- **WHEN** the response was lost and the user retries with the printed key
- **THEN** the command returns the original Agent, Job, Session, Input, and Turn identities
- **AND** no second Agent, Job, or Session is created

### Requirement: Output prints every participant identity

In table mode the command SHALL print the Agent identity (agent id and agent
name) together with the AgentJob, AgentSession, Input, and Turn identities,
the workspace, the status, and the canonical session, transcript, job, and
observation URLs — the `mo agent launch` output shape. In JSON mode the
command SHALL print the raw Server response unchanged.

#### Scenario: Table output lists the identities

- **WHEN** the launch succeeds in table mode
- **THEN** the output contains the agent id, agent name, job id, session id, input id, turn id, and the session URL

#### Scenario: JSON output is the Server projection

- **WHEN** the command runs with JSON output selected
- **THEN** the printed document is the Server's response with every projected identity and URL

### Requirement: Exit behavior is decisive and actionable

The command SHALL exit 0 only for an accepted launch, including an idempotent
replay, and non-zero for every rejection, printing an actionable error. An
unresolvable execution configuration SHALL name both repairs: pass
`--runtime`/`--model`/`--variant` or configure the Project default. A conflict
SHALL identify its cause, and a pending convergence SHALL instruct retrying
with the same key. A rejection MUST NOT leave local state that requires
cleanup.

#### Scenario: Missing execution configuration fails with guidance

- **WHEN** the Project has no default execution configuration and the command runs without execution hints
- **THEN** the command exits non-zero and prints the missing-configuration error naming both repairs
- **AND** no Agent is created on the Server

#### Scenario: Replay success exits zero

- **WHEN** a retry with the original idempotency key returns the recorded accepted outcome
- **THEN** the command prints the original identities and exits 0
