### Requirement: Versioned direct route surface

The direct API MUST expose the following routes under `/api/v1` with canonical Mohist IDs: `POST /projects/{projectId}/agents/{agentId}/launch`, `POST /projects/{projectId}/agent-sessions/{sessionId}/inputs`, `GET /projects/{projectId}/agent-jobs/{jobId}`, `GET /projects/{projectId}/agent-inputs/{inputId}`, `GET /projects/{projectId}/agent-turns/{turnId}`, `GET /projects/{projectId}/agent-sessions/{sessionId}/events`, and `POST /projects/{projectId}/agent-turns/{turnId}/stop`. The write routes MUST require `operator` scope, the read routes MUST require `readonly` or `operator` scope, and a command response MUST represent the durable keyed outcome rather than imply that execution has completed.

#### Scenario: Launch and follow-up through the direct surface
- **WHEN** an authorized caller submits a valid launch and then a valid input to the returned Session
- **THEN** the launch and input routes return public execution observations under the `/api/v1` contract, and the follow-up is attached to the canonical Session rather than creating a second Job

#### Scenario: Internal surface is not exported
- **WHEN** a caller requests a generic Session, Runner, Runtime, transcript, internal operation, or internal-event export route under the direct API
- **THEN** the route is unavailable and no internal read model or control identity is serialized

### Requirement: Strict external request validation

Direct launch and follow-up bodies MUST be JSON objects containing only a required, non-empty `text` string. The direct API MUST reject attachments, arbitrary context references, caller-selected execution options, unknown properties, duplicate JSON property names, invalid JSON, and invalid request headers with `400` before admission. The Turn-stop body MUST be empty. Input text MUST be retained only inside the canonical Session boundary and MUST NOT appear in a direct API response or public event.

#### Scenario: Unsupported launch field
- **WHEN** a launch body contains `text` plus an unknown property or a caller-selected Runtime
- **THEN** the Server returns `400 invalid_request` and creates no Job, Session, Input, Turn, mapping, queue entry, or external effect

#### Scenario: Invalid text body
- **WHEN** a launch or follow-up body is not an object, omits `text`, or supplies only an empty or whitespace text value
- **THEN** the Server returns `400 invalid_request` before canonical admission and exposes no submitted text in the response

#### Scenario: Stop body
- **WHEN** an authorized caller posts a stop request with a non-empty JSON body
- **THEN** the Server returns `400 invalid_request` and does not select a target or issue a stop effect

### Requirement: Canonical Agent ownership

The Server alone MUST create and update AgentJob, AgentSession, Session Input, Turn, queue, operation, and terminal facts. A successful launch MUST converge on at most one AgentJob and its initial Session, Input, and Turn; a prepared launch MUST remain addressable by its Job ID before Session acceptance. A follow-up MUST append one Input and either join or create the canonical Session Turn without creating another AgentJob. Direct stop MUST target exactly one canonical Turn and MUST NOT name a Runner, Runtime Session, dispatch attempt, workspace path, or provider operation.

#### Scenario: Prepared launch before Session acceptance
- **WHEN** a launch Job is durably prepared but the Session acceptance fact has not yet been projected
- **THEN** the Job read remains addressable with its Job ID, `status=accepted`, `jobStatus=preparing`, and null Session, Input, and Turn IDs

#### Scenario: Follow-up on an existing Session
- **WHEN** a valid follow-up is accepted for a known Session
- **THEN** exactly one new canonical Input/Turn relation is recorded according to Session admission, and no new AgentJob is created

#### Scenario: Caller attempts execution selection
- **WHEN** a launch request tries to select a Runner, Runtime, physical Runtime Session, model, Instructions, Skills, workspace path, or provider operation
- **THEN** the request is rejected as invalid and the selected Agent and canonical Session remain the only sources of those execution facts

### Requirement: Typed public-projection source facts

The canonical AgentJob and AgentSession producers MUST append typed
`ExternalAgentProjectionSourceFact` records to their durable outboxes in the
same aggregate transaction as every launch, follow-up, capacity, runner/result,
stop-fence, or committed context-boundary mutation that can affect the public
contract. Each fact MUST have a stable source identity, a monotonic source
revision for its Job/Session lineage, canonical IDs, public state components,
and only already-allowlisted output or error data. The source contract MUST
define these mappings: `job.prepared` updates the Job-anchored accepted
snapshot without creating a Session event before a Session exists;
`input.accepted` and `input.rejected` map to their corresponding public event
types; `turn.queued`, `turn.running`, `turn.outcome_pending`, and
`turn.terminal` map one-to-one; `session.unknown` records unresolved
acceptance, dispatch, binding, stop, or outcome facts without authorizing
replay; and `session.context_reset` is emitted only for a committed canonical
context boundary with a safe reason. The projector MUST NOT infer ordered
public transitions from a current aggregate snapshot or raw `AgentSessionEvents`
payload alone.

#### Scenario: Canonical lifecycle source inventory
- **WHEN** a canonical launch, follow-up, capacity decision, Runner/result update, stop-fence decision, or context boundary commits
- **THEN** the corresponding typed source fact is durable with the state needed to produce the defined public observation or event, and replaying the source fact does not create a second execution lifecycle

#### Scenario: Incomplete historical source history
- **WHEN** an authorized caller reads a known Session created before the typed source contract or otherwise lacking complete typed source history in the first release
- **THEN** the Server returns `503 projection_lag` with a safe projection-unavailable reason and no fabricated public events or snapshot transitions

### Requirement: Strict public execution projection

Every command and resource read MUST return only an allowlisted public execution object containing `projectId`, `agentId`, `jobId`, `sessionId`, `inputId`, `turnId`, `status`, `jobStatus`, `sessionActivity`, `admission`, `inputStatus`, `turnStatus`, `outcome`, `reasonCode`, `output`, `error`, `acceptedAt`, `queuedAt`, `startedAt`, `terminalAt`, `observedAt`, and `sequence`. Every listed key MUST be present. IDs and timestamps MUST be null only when their canonical fact does not exist; `observedAt` MUST always be present. `output` MUST contain only persisted public final text, and `error` MUST contain only a stable public code and safe message.

#### Scenario: Terminal public result
- **WHEN** a Turn reaches a durable completed, failed, cancelled, blocked, or rejected outcome
- **THEN** the public object reports `status=terminal`, the corresponding public outcome, safe output or error fields, terminal timestamp, and no raw provider result

#### Scenario: Privacy allowlist
- **WHEN** a caller reads a Job, Input, Turn, or command result
- **THEN** the response contains no prompt or input text, memory, Instructions, tool state, attachments, Runtime Session ID, Runner or Connection identity, workspace path, lease, fence, operation ID, attempt ID, dispatch detail, stack trace, or raw provider payload

### Requirement: Five-state public status

The aggregate `status` MUST be exactly one of `accepted`, `queued`, `running`, `terminal`, or `unknown`. A prepared Job or accepted Input without a queued, running, unresolved, or terminal fact MUST be `accepted`; queued work, including retryable capacity blocking, MUST be `queued`; running or `outcome_pending` work MUST be `running`; a durable rejection or terminal outcome MUST be `terminal`; and an unresolved acceptance, dispatch, binding, stop, or outcome fact MUST be `unknown`. A fenced terminal fact MUST take precedence over late non-terminal observations, and `unknown` MUST NOT authorize automatic replay. A first-release public projection MUST only serve Sessions with complete typed source history; a known ineligible historical Session follows the `projection_lag` behavior above.

#### Scenario: Retryable capacity block
- **WHEN** accepted work is waiting for execution capacity and no unresolved or terminal fact exists
- **THEN** the public status remains `queued` with `admission=blocked` and a safe public reason, rather than becoming terminal or unknown

#### Scenario: Outcome pending
- **WHEN** the Turn is known to have started but its final result is not yet confirmed
- **THEN** the public status is `running`, `turnStatus=outcome_pending`, and admission remains blocked without fabricated output

#### Scenario: Unresolved external effect
- **WHEN** the Server has consumed the durable facts and cannot confirm acceptance, dispatch, binding, stop, or outcome
- **THEN** the public status is `unknown`, the applicable component state is `unknown`, and the Server does not create replacement work merely because the caller polls or reconnects

#### Scenario: Terminal Turn in an active Session
- **WHEN** a requested Turn is terminal but a later Turn in the same Session is queued or running
- **THEN** the requested Turn remains `terminal`; Session activity is reported separately and does not rewrite the target Turn's outcome

### Requirement: Safe public errors and projection lag

Direct API errors MUST use a safe error envelope with a stable public code and message. Invalid requests MUST return `400`; an authorized missing resource MUST return `404`; a conflicting keyed request MUST return `409`; and a known canonical request or resource whose required public projection checkpoint is behind MUST return `503 projection_lag`. Projection lag MUST NOT be represented as public execution `unknown`, and no error message may contain a stack trace, provider error, filesystem path, or opaque internal identity.

#### Scenario: Projection has not caught up
- **WHEN** an authorized read or keyed retry requires a durable source watermark that the public projection has not yet committed
- **THEN** the Server returns `503 projection_lag` without admission or external effects, and the caller can retry the same key or read

#### Scenario: Durable admission rejection
- **WHEN** a well-formed keyed launch or follow-up is definitively rejected before execution
- **THEN** the command returns `200` with `status=terminal`, `outcome=rejected`, and a safe public reason or error instead of converting the durable decision into a transient transport failure
