## ADDED Requirements

### Requirement: CLI provides mo agent session list command

The CLI SHALL provide `mo agent session list <agent>` to list the generic `AgentSession`s belonging to a project-scoped Agent profile. The command SHALL accept the agent identity (name or `agent_*` id) as a positional argument and SHALL send `GET /api/projects/:projectId/agents/:agentId/sessions`, honoring an optional `--status <status>` flag that filters the result (covering at least `running`, `completed`, `failed`, `stopped`). In `-o table` mode the CLI SHALL render one row per session, surfacing at least the session id, status, created time, and resolved model, and SHALL group or annotate rows so the user can distinguish running, failed, and ended sessions. In `-o json` mode the CLI SHALL print the raw server payload without omission. The command SHALL accept `--project/--project-id` and `-o table|json`.

#### Scenario: List prints an agent's sessions

- **WHEN** the user runs `mo agent session list reviewer`
- **THEN** the CLI resolves `reviewer` to an agent id in the project
- **AND** sends `GET /api/projects/:projectId/agents/:agentId/sessions`
- **AND** prints one row per session including at least the session id, status, created time, and resolved model

#### Scenario: List with a status filter

- **WHEN** the user runs `mo agent session list reviewer --status failed`
- **THEN** the CLI sends `GET /api/projects/:projectId/agents/:agentId/sessions?status=failed`
- **AND** prints only that agent's failed sessions

#### Scenario: List in JSON emits the raw payload

- **WHEN** the user runs `mo agent session list reviewer -o json`
- **THEN** the CLI SHALL print the raw server response payload as JSON

#### Scenario: List unknown agent surfaces server error

- **WHEN** the user runs `mo agent session list nope`
- **AND** the server returns `404` because agent `nope` does not resolve in the project
- **THEN** the CLI prints the server-provided error message
- **AND** does not report silent success
- **AND** exits with a non-zero status

### Requirement: CLI provides mo agent session show command

The CLI SHALL provide `mo agent session show <sessionId>` to read the summary of a generic `AgentSession`. The command SHALL accept the session id as a positional argument and SHALL send `GET /api/projects/:projectId/agent-sessions/:sessionId`. In `-o table` mode the CLI SHALL render a human-readable summary surfacing the agent id and agent name, status, created and last-activity times, resolved model, usage, failure category (when present), tool call and tool error counts, and any recorded context references (issue, epic, repository, workspace path). In `-o json` mode the CLI SHALL print the raw server payload without omission. The command SHALL accept `--project/--project-id` and `-o table|json`. When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status. The command SHALL be distinct from the existing `mo issue session show <num> <name>` workflow-session verb.

#### Scenario: Show renders the generic session summary

- **WHEN** the user runs `mo agent session show sess_123 -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/agent-sessions/sess_123`
- **AND** the rendered output SHALL present the agent identity, status, created and last-activity times, resolved model, usage, failure category (when present), tool call and tool error counts, and recorded context references

#### Scenario: Show in JSON emits the raw payload

- **WHEN** the user runs `mo agent session show sess_123 -o json`
- **THEN** the CLI SHALL print the server response as JSON

#### Scenario: Show nonexistent session surfaces error

- **WHEN** the user runs `mo agent session show nope`
- **AND** the server returns `404` for session `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status

#### Scenario: Show is distinct from the workflow session verb

- **WHEN** the user runs `mo agent session show <sessionId>`
- **THEN** the CLI SHALL target the generic-session summary endpoint
- **AND** SHALL NOT invoke the existing `mo issue session show <num> <name>` workflow-session verb
- **AND** the existing workflow-session verb SHALL remain unchanged

### Requirement: CLI provides mo agent session transcript command

The CLI SHALL provide `mo agent session transcript <sessionId>` to read the transcript of a generic `AgentSession`. The command SHALL accept the session id as a positional argument and SHALL send `GET /api/projects/:projectId/agent-sessions/:sessionId/transcript`. In `-o table` mode the CLI SHALL render a summary (turn count / part count, first and last activity timestamps) rather than dumping every message, consistent with `mo issue session transcript` table-mode behavior. In `-o json` mode the CLI SHALL print the full transcript in its raw server JSON shape. The command SHALL accept `--project/--project-id` and `-o table|json`. When the session does not exist, the CLI SHALL print the server-provided error and exit with a non-zero status.

#### Scenario: Transcript table mode renders a summary

- **WHEN** the user runs `mo agent session transcript sess_123 -o table`
- **THEN** the CLI sends `GET /api/projects/:projectId/agent-sessions/sess_123/transcript`
- **AND** the rendered output SHALL present a summary including the part count and first/last activity timestamps
- **AND** SHALL NOT dump every individual message body

#### Scenario: Transcript JSON mode emits the full transcript

- **WHEN** the user runs `mo agent session transcript sess_123 -o json`
- **THEN** the CLI SHALL print the full transcript payload as returned by the server

#### Scenario: Transcript nonexistent session surfaces error

- **WHEN** the user runs `mo agent session transcript nope`
- **AND** the server returns `404` for session `nope`
- **THEN** the CLI SHALL print the server-provided error message
- **AND** SHALL exit with a non-zero status
