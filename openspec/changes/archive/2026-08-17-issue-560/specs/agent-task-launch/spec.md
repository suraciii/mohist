### Requirement: One accepted request creates the Agent definition and starts the first execution

The Server SHALL expose one task-first create-and-launch route
(`POST /api/projects/{projectRef}/agent-tasks`). One accepted request carries
the task — a `prompt`, optional `attachments`, and optional `context`
references — together with optional identity and execution hints, creates a
complete Agent definition for the task, and starts the first AgentJob and
AgentSession. One accepted request SHALL produce exactly one Agent, one
AgentJob, one AgentSession, and the first SessionInput and AgentTurn, and
SHALL require an `Idempotency-Key` header under the same rule as the
definition-first launch route.

#### Scenario: A task alone creates and launches

- **WHEN** the caller posts a non-empty prompt and the execution configuration is resolvable from caller hints or the Project default
- **THEN** the Server creates one Agent definition for the task, starts the first AgentJob and AgentSession, and returns the created Agent's identity together with the Job, Session, Input, and Turn identities
- **AND** no second Agent, Job, or Session is created

#### Scenario: Hints override derivation

- **WHEN** the request supplies `name` or execution hints (`runtime`, `model`, `variant`)
- **THEN** the created Agent definition uses the supplied values instead of the derived defaults
- **AND** every unspecified part is still derived

#### Scenario: A missing idempotency key is rejected

- **WHEN** the request omits the `Idempotency-Key` header
- **THEN** the Server rejects with `idempotency_key_required` and creates nothing

#### Scenario: An unusable task is rejected

- **WHEN** the request has neither usable prompt text nor an accepted attachment
- **THEN** the Server rejects with `input_required` and creates nothing

### Requirement: The request surface is closed and validated before creation

The task-first request SHALL accept exactly the top-level fields `prompt`,
`attachments`, `context`, `name`, `runtime`, `model`, and `variant`. The
`context` object SHALL accept the same reference fields as the
definition-first launch body and SHALL be validated by the same rules. Any
undeclared top-level field SHALL be rejected with an actionable 400
(`unsupported_field`) naming the field, and malformed hints — a runtime
outside `opencode`/`pi` or a model reference without the `provider/model`
form — SHALL be rejected with a validation error. Every such rejection SHALL
occur before any Agent, AgentSession, or AgentJob is created.

#### Scenario: An undeclared field is rejected

- **WHEN** the body contains a top-level field outside the accepted set (for example `instructions`)
- **THEN** the Server responds 400 `unsupported_field` naming the field
- **AND** no Agent, Session, or Job is created

#### Scenario: A malformed execution hint is rejected

- **WHEN** the request supplies `runtime: "fast"` or `model: "gpt"`
- **THEN** the Server responds 400 with a validation error identifying the offending hint
- **AND** no Agent is created

#### Scenario: Context references resolve by the launch rules

- **WHEN** the request carries a context reference to an unknown Issue, Epic, or Workspace
- **THEN** the Server rejects with the same status and error code the definition-first launch route returns for that reference
- **AND** no Agent is created

### Requirement: Definition creation composes the canonical launch path

The task-first operation SHALL create the Agent definition and start execution
through the same canonical launch pipeline as the definition-first launch
route — the idempotency-keyed launch coordinator with minted Session, Input,
and Turn identities — and MUST NOT introduce a third execution path. The
resulting AgentSession and AgentJob SHALL be indistinguishable from a
definition-first launch of the created Agent: the same session metadata and
source labels, the same launch-time-fixed definition snapshot, the same
workspace binding rules including launch-origin defaults, and the same read
surfaces.

#### Scenario: The created session is a canonical agent launch

- **WHEN** a task-first launch completes
- **THEN** its AgentSession carries the same agent-launch metadata (project, agent id, agent name) as a definition-first launch of that Agent
- **AND** the AgentJob and session read surfaces accept the returned identities without translation

#### Scenario: A later definition-first launch shares the pipeline

- **WHEN** the created Agent is launched again through the definition-first launch route
- **THEN** both executions enter one launch pipeline and differ only in their launch entry

### Requirement: A rejected request leaves no orphan Agent

A rejected task-first request MUST NOT leave a created Agent, AgentJob,
AgentSession, SessionInput, or AgentTurn that the user must clean up.
Determinable rejections — request shape, context resolution, unusable input,
name conflict, and unresolvable execution configuration — SHALL be evaluated
before the definition is created. If a composed launch is terminally rejected
after the definition was created, the Server SHALL remove the created Agent
from the active set so no active Agent remains from the rejected request.

#### Scenario: A conflicting name is rejected without creation

- **WHEN** the request supplies a `name` already used by another Agent in the Project
- **THEN** the Server responds 409 `AGENT_NAME_CONFLICT`
- **AND** no Agent is created

#### Scenario: Unresolvable execution configuration is rejected with guidance

- **WHEN** the request supplies no execution hints and the Project has no default execution configuration
- **THEN** the Server rejects with an actionable error that identifies the missing execution configuration and names both repairs (supply hints or configure the Project default)
- **AND** no Agent is created

#### Scenario: A terminal launch rejection after creation rolls the definition back

- **WHEN** the launch converges to a terminal rejection after the definition was created
- **THEN** the Server records the rejection under the idempotency key and leaves no active Agent from the request

### Requirement: Idempotent replay follows the launch convergence rules

Replaying the same `Idempotency-Key` with the same request SHALL return the
original outcome: the original identities for an accepted operation, or the
original recorded rejection for a rejected one. A replay under the same key
with a different request fingerprint — a changed prompt, context,
attachments, `name`, or execution hints — SHALL be rejected 409
`launch_idempotency_conflict`. A still-converging plan SHALL respond 503
`launch_setup_pending` carrying the idempotency key so the caller retries with
the same key. A replay MUST NOT create a second Agent, AgentJob, or
AgentSession.

#### Scenario: Replay after response loss returns the original identities

- **WHEN** the same accepted request is replayed under the same idempotency key
- **THEN** the Server returns the original Agent, Job, Session, Input, and Turn identities
- **AND** no duplicate Agent, Job, or Session is created

#### Scenario: A conflicting replay is rejected

- **WHEN** the same idempotency key is replayed with a different prompt, or with different execution hints — a changed, added, or removed `runtime`, `model`, or `variant`
- **THEN** the Server responds 409 `launch_idempotency_conflict`

#### Scenario: A rejected plan stays rejected

- **WHEN** a request whose plan recorded a terminal rejection is replayed with the same key and the same body
- **THEN** the Server returns the recorded rejection and creates nothing

### Requirement: The response projects every participant identity

A 201 response SHALL project the created Agent identity (`agentId`,
`agentName`) together with `jobId`, `sessionId`, `inputId`, `turnId`,
`workspaceId`, `targetId`, `origin`, `status`, the attachment results, and the
canonical `sessionUrl`, `transcriptUrl`, `jobUrl`, and `observationUrl`,
mirroring the definition-first launch response projection.

#### Scenario: The accepted response carries all identities

- **WHEN** the task-first request is accepted
- **THEN** the response contains the Agent, Job, Session, Input, and Turn identities, the workspace identity, and the session, transcript, job, and observation URLs
- **AND** `sessionUrl` addresses the created AgentSession page
