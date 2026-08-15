### Requirement: PublicExecutionRead is a strict allowlist

Every command and resource-read response SHALL return `PublicExecutionRead`
with every listed key present and no unlisted execution property: `projectId`,
`agentId`, `jobId`, `sessionId`, `inputId`, `turnId`, `status`, `jobStatus`,
`sessionActivity`, `admission`, `inputStatus`, `turnStatus`, `outcome`,
`reasonCode`, `output`, `error`, `acceptedAt`, `queuedAt`, `startedAt`,
`terminalAt`, `observedAt`, and `sequence`. IDs and timestamps MAY be null
only where the canonical fact does not exist (`jobId` is null for a follow-up;
Input and Turn IDs are null for a durable rejection that created no live
records; `sequence` is null only when no Session public event could exist).
`observedAt` MUST always be present. The response MUST NOT be a serialized
internal read shape and MUST never expose, without exception:
`runtimeSessionId`, Runner IDs, runtime names, binding epochs, connection IDs,
leases, fences, operation IDs, attempt IDs, dispatch or retry details, prompt
or input text, instructions, memory, tool state, workspace/workdir/path,
attachments, raw payloads, transcript facts, or raw provider or Runner errors.

#### Scenario: Response contains exactly the allowed fields

- **WHEN** any authorized direct read or command returns a `PublicExecutionRead`
- **THEN** every allowlisted key is present in the response body
- **AND** no unlisted execution property appears

#### Scenario: Prompt text never leaves the boundary

- **WHEN** a caller reads the Job launched with text `Investigate the failed deployment`
- **THEN** the response contains no field carrying that text or any other input content

#### Scenario: Safe public error replaces internal detail

- **WHEN** a Turn fails with an internal provider error containing a stack trace and path
- **THEN** the response `error` carries only a stable public `code` and safe public `message`
- **AND** the stack trace, path, and provider detail are absent

### Requirement: Field values follow the public vocabulary

`status` SHALL be one of `accepted`, `queued`, `running`, `terminal`,
`unknown`. Component fields SHALL use only their public values: `jobStatus` in
`preparing`, `queued`, `running`, `terminal`, `unknown`, or null;
`sessionActivity` in `idle`, `active`, `unknown`, or null; `admission` in
`ready`, `blocked`, or null; `inputStatus` in `accepted`, `rejected`,
`unknown`, or null; `turnStatus` in `queued`, `running`, `outcome_pending`,
`terminal`, `unknown`, or null; `outcome` in `completed`, `rejected`,
`failed`, `cancelled`, `blocked`, or null. `output` SHALL be null or
`{ "text": "..." }` containing only persisted public final output, never a
transcript or raw provider response. `reasonCode` SHALL be null or one stable
safe public reason code (for example `queue_full`, `context_reset`, or
`stop_outcome_unknown`) that explains a public status without internal cause
detail. Timestamps SHALL be RFC 3339 UTC.

#### Scenario: Component facts stay visible beside the aggregate

- **WHEN** a retryable dispatch block keeps a Turn queued behind admission
- **THEN** the response reports `status=queued`, `admission=blocked`, and a safe public error while `turnStatus` remains `queued`

### Requirement: The five-state aggregate follows fixed precedence

`status` SHALL be derived from canonical facts with this fixed precedence:

1. A durable terminal fact protected by the target Turn's terminal fence wins; late Runner, stop, or event-bus observations cannot move the Turn back or replace its output or error.
2. A durable rejection is terminal with `outcome=rejected`, even when it has no live Input or Turn ID.
3. Without a terminal fact, any unresolved canonical acceptance, dispatch, binding, stop, or outcome fact yields `unknown`, with at least one applicable component fact `unknown` and `admission=blocked` whenever a Session exists.
4. `outcome_pending` is `running`, never terminal; it is shown explicitly in `turnStatus` and always carries `admission=blocked`.
5. A retryable dispatch blocked state remains `queued` with `admission=blocked`; only a terminal Turn or Job outcome of `blocked` becomes terminal.
6. Otherwise a running fact wins over `queued`, and `queued` wins over `accepted`.

`unknown` and `outcome_pending` SHALL NOT authorize automatic replay; a public
client reconnect, poll, or repeated different key MUST NOT cause a new Job,
Input, Turn, dispatch attempt, or stop.

#### Scenario: outcome_pending maps to running, never terminal

- **WHEN** a Turn's stop is unresolved and its outcome cannot be confirmed while the Turn runs on
- **THEN** the response reports `status=running` with `turnStatus=outcome_pending` and `admission=blocked`

#### Scenario: Unresolved facts yield unknown

- **WHEN** the Server cannot confirm the dispatch or binding facts for the current Turn and no fenced terminal fact resolves it
- **THEN** the response reports `status=unknown` with the applicable component fact `unknown` and `admission=blocked`

#### Scenario: Running wins over queued, which wins over accepted

- **WHEN** an Input is durably accepted and its Turn is canonically running with no unresolved fact
- **THEN** the response reports `status=running` with `inputStatus=accepted` and `turnStatus=running`

### Requirement: Job reads anchor to the canonical Job projection

`GET /api/v1/projects/{projectId}/agent-jobs/{jobId}` SHALL return
`PublicExecutionRead` anchored to the canonical Job's durable public
projection, never a raw Job launch shape. A prepared Job whose Session is not
yet accepted returns `200` with its `jobId`, `status=accepted`,
`jobStatus=preparing`, and null `sessionId`/`inputId`/`turnId`. A durable
Session rejection returns `200` with the same `jobId`, `status=terminal`,
`outcome=rejected`, a safe error and reasonCode, and null live IDs. After
acceptance the same read exposes its public Session/Input/Turn references and
later public status, output, or error as they become projected. If a launch
response was lost before the caller learned the `jobId`, repeating the launch
with the same Idempotency-Key returns the same Job anchor or
`projection_lag`; it MUST NOT create a replacement Job. A Job absent from or
not belonging to the authorized Project returns `404 job_not_found`.

#### Scenario: Prepared launch observation

- **WHEN** the caller reads a Job whose canonical prepare fact is durable but whose Session acceptance is still pending
- **THEN** the response is `200` with `jobId` set, `status=accepted`, `jobStatus=preparing`, and null `sessionId`, `inputId`, and `turnId`

#### Scenario: Durable rejection observation

- **WHEN** the caller reads a Job whose Session was durably rejected
- **THEN** the response is `200` with that `jobId`, `status=terminal`, `outcome=rejected`, a safe public error, and null live Session/Input/Turn IDs

#### Scenario: Lost launch response recovers the Job anchor

- **WHEN** a launch response is lost and the caller repeats the launch with the same key
- **THEN** the retry returns the original Job mapping and its current public observation, or `503 projection_lag`
- **AND** no replacement Job is created

### Requirement: Input and Turn reads anchor to their canonical records

Input and Turn reads SHALL be anchored to the requested canonical record. A
terminal target SHALL remain terminal even when the enclosing Session is
active because a later Turn is queued or running, and an active Session MUST
NOT turn a terminal Job or Turn into `running`. `sessionActivity` is context,
not a replacement for the requested Input/Turn outcome. An Input or Turn
absent from or not belonging to the authorized Project returns the matching
`404 input_not_found` or `404 turn_not_found`.

#### Scenario: Terminal Turn inside an active Session

- **WHEN** the caller reads a Turn that reached a fenced terminal outcome while a later Turn in the same Session is running
- **THEN** the response reports `status=terminal` with that Turn's outcome, and `sessionActivity=active` remains visible as context

#### Scenario: Unknown Input in a foreign Project

- **WHEN** an authorized caller reads an Input ID that does not exist in the authorized Project
- **THEN** the response is `404 input_not_found`

### Requirement: Command responses use the same public shape

Launch, follow-up, and stop SHALL return `200` once their durable keyed
outcome is known, carrying `PublicExecutionRead` for the unique mapping. A
`200` command response does not mean execution completed; the body state is
authoritative. A canonical admission rejection MUST NOT be hidden as an HTTP
transport failure: a well-formed keyed launch or follow-up that receives a
durable rejection returns `200` with `status=terminal` and `outcome=rejected`
plus a safe public error, so a response-loss replay returns the same durable
decision. Stop responses expose only the public Turn observation and safe
reasonCode, never the frozen target, binding, deadline, owner, lease, fence,
or internal operation ID.

#### Scenario: Durable rejection is a 200, not a 5xx

- **WHEN** a well-formed keyed launch is durably rejected at admission
- **THEN** the response is `200` with `status=terminal`, `outcome=rejected`, and a safe public error

#### Scenario: Stop response hides frozen internals

- **WHEN** a stop is issued for a running Turn
- **THEN** the response exposes only the public Turn observation and a safe reasonCode, with no frozen target, binding, deadline, owner, lease, fence, or operation ID
