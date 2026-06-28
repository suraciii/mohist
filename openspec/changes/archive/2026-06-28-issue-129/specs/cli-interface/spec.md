## ADDED Requirements

### Requirement: CLI provides mo agent session launch command

The CLI SHALL provide `mo agent session launch <agent>` to launch a generic `AgentSession` from a project-scoped Agent profile. The command SHALL accept the agent identity (name or `agent_*` id) as a positional argument and a required `--prompt <text>` flag (or `--prompt-file <path>` / `--prompt-stdin` for long prompts) carrying the user's prompt. The command SHALL send `POST /api/projects/:projectId/agents/:agentId/sessions` with the prompt (and optional context) and SHALL print the new session id, the agent id/name, and the current session status. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Launch prints the new session id and status

- **WHEN** the user runs `mo agent session launch reviewer --prompt "Audit the auth flow"`
- **THEN** the CLI resolves `reviewer` to an agent id in the project
- **AND** sends `POST /api/projects/:projectId/agents/:agentId/sessions` with the prompt
- **AND** prints the new session id, the agent id/name, and the current session status

#### Scenario: Launch reads the prompt from a file

- **WHEN** the user runs `mo agent session launch reviewer --prompt-file task.md`
- **THEN** the CLI reads `task.md` as UTF-8 text
- **AND** sends the file contents as the prompt in the launch request body

#### Scenario: Launch reads the prompt from stdin

- **WHEN** the user runs `echo "summarize this" | mo agent session launch reviewer --prompt-stdin`
- **THEN** the CLI reads the prompt from standard input
- **AND** sends the stdin contents as the prompt in the launch request body

#### Scenario: Launch in JSON emits the raw payload

- **WHEN** the user runs `mo agent session launch reviewer --prompt "Hi" -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Missing prompt fails clearly

- **WHEN** the user runs `mo agent session launch reviewer` without `--prompt`, `--prompt-file`, or `--prompt-stdin`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

#### Scenario: Unknown agent surfaces server error

- **WHEN** the user runs `mo agent session launch nope --prompt "Hi"`
- **AND** the server returns `404` because agent `nope` does not resolve in the project
- **THEN** the CLI prints the server-provided error message
- **AND** does not report silent success
- **AND** exits with a non-zero status

### Requirement: CLI provides mo agent session followup command

The CLI SHALL provide `mo agent session followup <sessionId>` to send a free-text followup to a running generic `AgentSession`. The command SHALL accept the session id as a positional argument and a required `--text <text>` flag (or `--text-file <path>` / `--text-stdin` for long messages) carrying the followup text. The command SHALL send `POST /api/projects/:projectId/agent-sessions/:sessionId/followup` with the text and SHALL print the delivery status. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Followup prints the delivery status

- **WHEN** the user runs `mo agent session followup sess_123 --text "add a logout route"`
- **THEN** the CLI sends `POST /api/projects/:projectId/agent-sessions/sess_123/followup` with the text
- **AND** prints the delivery status returned by the server

#### Scenario: Followup reads the text from a file

- **WHEN** the user runs `mo agent session followup sess_123 --text-file note.md`
- **THEN** the CLI reads `note.md` as UTF-8 text
- **AND** sends the file contents as the followup text

#### Scenario: Followup in JSON emits the raw payload

- **WHEN** the user runs `mo agent session followup sess_123 --text "Hi" -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Terminal session surfaces conflict

- **WHEN** the user runs `mo agent session followup sess_123 --text "Hi"`
- **AND** the server returns `409` because the session is no longer active
- **THEN** the CLI prints the server-provided error message
- **AND** SHALL NOT report success
- **AND** exits with a non-zero status

#### Scenario: Missing text fails clearly

- **WHEN** the user runs `mo agent session followup sess_123` without `--text`, `--text-file`, or `--text-stdin`
- **THEN** the CLI prints a clear validation error
- **AND** exits with code 1

### Requirement: CLI provides mo agent session cancel command

The CLI SHALL provide `mo agent session cancel <sessionId>` to request cancellation of a running generic `AgentSession` by sending `POST /api/projects/:projectId/agent-sessions/:sessionId/cancel`. The command SHALL print the resulting session state returned by the server. When the server reports the session is not currently cancellable, the CLI SHALL surface that state to the user rather than reporting success. When the server reports the session is already terminal, the CLI SHALL surface the terminal state. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: Cancel prints the resulting session state

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server cancels the running turn
- **THEN** the CLI prints the resulting session state returned by the server

#### Scenario: Non-cancellable session is surfaced honestly

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server reports the session is not currently cancellable
- **THEN** the CLI SHALL surface that state to the user
- **AND** SHALL NOT report success

#### Scenario: Terminal session surfaces terminal state

- **WHEN** the user runs `mo agent session cancel sess_123`
- **AND** the server reports the session is already in a terminal state
- **THEN** the CLI SHALL surface the terminal state
- **AND** SHALL NOT report a fresh cancellation

#### Scenario: Unknown session surfaces server error

- **WHEN** the user runs `mo agent session cancel nope`
- **AND** the server returns `404` because session `nope` does not exist
- **THEN** the CLI prints the server-provided error message
- **AND** exits with a non-zero status
