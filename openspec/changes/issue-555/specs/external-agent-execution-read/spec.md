### Requirement: The /api/v1 execution route surface

The Server SHALL expose exactly this direct external Agent surface under `/api/v1`, addressed by canonical Mohist IDs and not display names: `POST /api/v1/projects/{projectId}/agents/{agentId}/launch`, `POST /api/v1/projects/{projectId}/agent-sessions/{sessionId}/inputs`, `POST /api/v1/projects/{projectId}/agent-turns/{turnId}/stop`, `GET /api/v1/projects/{projectId}/agent-jobs/{jobId}`, `GET /api/v1/projects/{projectId}/agent-inputs/{inputId}`, `GET /api/v1/projects/{projectId}/agent-turns/{turnId}`, and `GET /api/v1/projects/{projectId}/agent-sessions/{sessionId}/events`. A command route SHALL return 200 once its durable keyed outcome is known; the 200 does not mean execution completed and the body state is authoritative. The API MUST NOT add a route for selecting a Runner, Runtime, workspace, physical Runtime Session, prompt memory, model, instructions, Skills, a provider operation, a generic Session or operation lookup, transcript export, or internal-event export, and it MUST NOT create a second execution lifecycle, queue, or event bus.

#### Scenario: A launch returns the durable keyed outcome

- **WHEN** an authorized caller posts a valid keyed launch
- **THEN** the route SHALL return 200 with one `PublicExecutionRead` for the unique launch mapping
- **AND** the response's `status` field, not the HTTP code, SHALL be authoritative for execution progress

#### Scenario: The surface adds no internal control routes

- **WHEN** the direct route surface is enumerated
- **THEN** it SHALL contain exactly the seven v1 routes and no Runner, Runtime, workspace, transcript, operation, or internal-event route

### Requirement: PublicExecutionRead is a strict allowlist

Every command and resource-read route SHALL return exactly one `PublicExecutionRead` object whose properties are exactly the allowlist: `projectId`, `agentId`, `jobId`, `sessionId`, `inputId`, `turnId`, `status`, `jobStatus`, `sessionActivity`, `admission`, `inputStatus`, `turnStatus`, `outcome`, `reasonCode`, `output`, `error`, `acceptedAt`, `queuedAt`, `startedAt`, `terminalAt`, `observedAt`, `sequence`. Every listed key SHALL be present in every response; IDs and timestamps MAY be null only where the canonical fact does not exist; `observedAt` SHALL always be present; `sequence` SHALL be null only when no Session public event could exist; `jobId` is null for a follow-up. `output` SHALL be null or `{ "text": "..." }` containing only persisted public final output, never a transcript or raw provider response. `error` SHALL be null or `{ "code", "message" }` with a stable public code and safe explanation. `reasonCode` SHALL be null or one stable safe public reason code. No response SHALL contain an unlisted execution property, and the direct API MUST NOT serialize `AgentJobLaunchRead`, `AgentSessionRead`, `SessionInputRead`, `TurnResultRead`, or `SessionOperationRead` to an external caller.

#### Scenario: Every response carries every allowlisted key and nothing else

- **WHEN** any command or resource-read route succeeds
- **THEN** the response body SHALL contain all 22 allowlisted keys
- **AND** it SHALL NOT contain any property outside the allowlist

#### Scenario: Internal execution facts never leak

- **WHEN** a `PublicExecutionRead` is produced for a running Turn with an active Runner binding
- **THEN** the response MUST NOT expose runtimeSessionId, Runner IDs, runtime names, binding epochs, connection IDs, leases, fences, operation IDs, attempt IDs, dispatch or retry details, Instructions, memory, tool state, workspace, path, attachments, raw payloads, transcript facts, or raw provider or Runner errors

#### Scenario: Errors are safe public envelopes

- **WHEN** a failed execution is read
- **THEN** `error` SHALL carry only a stable public code and safe message
- **AND** it MUST NOT carry a stack trace, provider error, path, or opaque internal identity

#### Scenario: Prompt text never returns

- **WHEN** a caller launches with body text and then reads the Job, Input, Turn, or event routes
- **THEN** no direct API response or public event SHALL contain the prompt or input text

### Requirement: One five-state aggregate with fixed precedence

`status` SHALL aggregate canonical facts into exactly one of `accepted`, `queued`, `running`, `terminal`, or `unknown`, and the component facts `jobStatus`, `sessionActivity`, `admission`, `inputStatus`, `turnStatus`, and `outcome` SHALL remain visible so a caller does not lose blocked, rejected, or `outcome_pending` facts behind one label. The precedence SHALL be fixed and applied in this order: a durable terminal fact protected by the target Turn's terminal fence wins and late Runner, stop, or event-bus observations cannot move that Turn back to a non-terminal state or replace its output or error; a durable rejection is terminal with `outcome=rejected` even when no live Input or Turn ID exists; without a terminal fact, any unresolved canonical acceptance, dispatch, binding, stop, or outcome fact projects `unknown`; `outcome_pending` is running, never terminal, is shown in `turnStatus`, and always has `admission=blocked`; a retryable dispatch block remains `queued` with `admission=blocked` rather than terminal, and only a terminal Turn or Job outcome of `blocked` becomes terminal; otherwise a running fact wins over queued and queued wins over accepted. An Input or Turn read SHALL be anchored to its requested canonical record: a terminal target remains terminal even when the enclosing Session is active, and `sessionActivity` is context, not a replacement for the requested outcome. `unknown` and `outcome_pending` MUST NOT authorize automatic replay: the Server SHALL NOT create a new Job, Input, Turn, dispatch attempt, or stop merely because a public client reconnects, polls, or repeats a different key.

#### Scenario: The terminal fence wins over late observations

- **WHEN** a Turn has a fenced terminal fact and a late Runner result or stop observation arrives
- **THEN** the public projection SHALL keep that Turn terminal with its recorded outcome, output, and error
- **AND** the late observation MUST NOT move the Turn back to a non-terminal state or replace its recorded facts

#### Scenario: A durable rejection is terminal without live IDs

- **WHEN** a launch was durably rejected before Session acceptance
- **THEN** its public observation SHALL report `status=terminal` and `outcome=rejected`
- **AND** `sessionId`, `inputId`, and `turnId` SHALL be null because no live records exist

#### Scenario: An unresolved fact projects unknown

- **WHEN** the projector has consumed the required durable facts and those facts cannot confirm acceptance, dispatch, binding, stop, or outcome
- **THEN** `status` SHALL be `unknown` with at least one applicable component fact `unknown` and `admission=blocked` whenever a Session exists

#### Scenario: outcome_pending is running with blocked admission

- **WHEN** the target Turn is `outcome_pending`
- **THEN** `status` SHALL be `running` and `turnStatus` SHALL be `outcome_pending`
- **AND** `admission` SHALL be `blocked` and no final output SHALL be implied

#### Scenario: A retryable dispatch block stays queued

- **WHEN** the current Job or Turn has a retryable dispatch block with a valid retry remaining
- **THEN** `status` SHALL be `queued` with `admission=blocked` and a safe public error or reason
- **AND** the target MUST NOT be reported terminal

#### Scenario: A terminal Turn inside an active Session stays terminal

- **WHEN** an Input or Turn read targets a terminal record whose enclosing Session is active because a later Turn is queued or running
- **THEN** the requested target SHALL remain terminal
- **AND** `sessionActivity` SHALL be reported as context without changing the requested outcome

### Requirement: Job read is the status recovery path

`GET /agent-jobs/{jobId}` SHALL return the same strict `PublicExecutionRead` anchored to the canonical Job's durable public projection, never `AgentJobLaunchRead` or another raw Job shape. A prepared Job whose Session is not yet accepted SHALL return 200 with its `jobId`, `status=accepted`, `jobStatus=preparing`, and null `sessionId`/`inputId`/`turnId`. A durable Session rejection SHALL return 200 with that same `jobId`, `status=terminal`, `outcome=rejected`, safe `error`/`reasonCode`, and null live IDs. After acceptance, the same Job read SHALL expose its public Session/Input/Turn references and later public status, output, or error as they become projected. A PAT without the selected Project grant receives 403 before Job lookup; an authorized Project whose Job is absent or does not belong to it receives 404 `job_not_found`. If a launch response was lost before the caller learned the `jobId`, repeating the launch with the same Idempotency-Key SHALL return the same Job anchor or `projection_lag`; the Server MUST NOT create a replacement Job.

#### Scenario: A prepared Job reads before Session acceptance

- **WHEN** an authorized caller reads a Job that is durably prepared while Session acceptance is pending
- **THEN** the route SHALL return 200 with the `jobId`, `status=accepted`, `jobStatus=preparing`, and null `sessionId`, `inputId`, and `turnId`

#### Scenario: A rejected launch reads through its Job

- **WHEN** an authorized caller reads a Job whose Session was durably rejected
- **THEN** the route SHALL return 200 with `status=terminal`, `outcome=rejected`, a safe `error`/`reasonCode`, and null live IDs

#### Scenario: A lost launch response recovers through retry and Job read

- **WHEN** a launch response was lost before the caller learned the `jobId`
- **THEN** repeating the launch with the same key SHALL return the same Job anchor or `503 projection_lag`
- **AND** the Job read SHALL then serve as the public status recovery path without creating a replacement Job

#### Scenario: An absent Job is 404 only after authorization

- **WHEN** an in-grant caller reads a Job that does not exist or belongs to another Project
- **THEN** the Server SHALL return 404 `job_not_found`

### Requirement: A durable checkpointed public projection serves all reads

The Server SHALL own one durable public projection per target Session, and a launch target before a Session exists SHALL be permanently anchored by `jobId` so its Job projection remains addressable before and after Session acceptance. A projector consuming canonical aggregate records plus their durable outbox facts SHALL persist, in one projection transaction: the allowlisted `PublicExecutionRead` snapshot for every affected public target; the corresponding public Session event journal entries and sequences; and the source checkpoint/watermark proving which durable outbox facts the snapshot and journal include. `PublicExecutionRead` and `PublicEventPage` SHALL be read only from this projection; they are mutually consistent at one recorded checkpoint and intentionally eventually consistent with the independent canonical aggregates. The projection MUST NOT claim atomic commitment with a combined Job/Session/Input/Turn canonical write. If an authorized route's required source watermark is ahead of the stored projection checkpoint, the route SHALL return `503 projection_lag` and MUST NOT return a stale state as current; projection lag is a transport and reconciliation condition, not the public state `unknown`, no new admission or effect occurs, and the caller retries the same key or read. A crash before the projection transaction commits SHALL leave no partial snapshot, sequence, or checkpoint, and restart SHALL replay the same durable outbox input; a crash after commit SHALL resume after the checkpoint and MUST NOT emit a second public sequence for the same normalized source transition. A Turn terminal projection SHALL store the canonical terminal fence/revision internally and MAY become terminal only after the current terminal fact passes that fence, so stale outbox facts, delayed Runner results, or replayed projector input cannot move that target back to a non-terminal public state.

#### Scenario: Snapshot, journal, and checkpoint commit together

- **WHEN** the projector persists a new public transition
- **THEN** the snapshot, event journal entries, event identity, next sequence, and source checkpoint SHALL commit in one transaction
- **AND** a crash at any point SHALL leave either all of them or none of them

#### Scenario: A required watermark ahead of the checkpoint is projection lag

- **WHEN** an authorized command or read requires a source watermark that is ahead of the stored projection checkpoint
- **THEN** the route SHALL return `503 projection_lag`
- **AND** the Server MUST NOT return the stale projection as current state, create a new admission, or produce an external effect

#### Scenario: Projection recovery does not duplicate sequences

- **WHEN** the projector restarts after a committed projection transaction
- **THEN** it SHALL resume after the recorded checkpoint
- **AND** it MUST NOT emit a second public sequence for the same normalized source transition

#### Scenario: Unknown comes only from consumed durable facts

- **WHEN** the projection reports `unknown` for a target
- **THEN** the projector SHALL have consumed the required durable facts and those facts SHALL say the fact cannot be confirmed
- **AND** projection lag or an unread outbox item MUST NOT be reported as `unknown`
