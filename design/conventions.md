# Conventions

## Identity

A domain identity is the smallest key that identifies an entity permanently and without
ambiguity. It does not have to be a single random ID. When an entity naturally belongs to a
parent scope, the parent identity and the number within that scope form the identity together.

- Project: `ProjectId` (for example `proj_123`).
- Issue: (`ProjectId`, `IssueNumber`) (for example (`proj_123`, `42`)).
- Epic: (`ProjectId`, `EpicNumber`) (for example (`proj_123`, `7`)).
- WorkflowRun: `WorkflowRunId` (for example `wr_123`).
- Runner: `RunnerId` (for example `runner_123`).
- AgentSession: `SessionId` (for example `session_123`).
- SessionOperation: `operationId` (for example `op_123`).
- Event: `EventId` (for example `evt_123`).
- Principal: `PrincipalId` (for example `prin_123`).
- Credential: `CredentialId` (for example `cred_123`).

- An Issue or Epic number is a permanent part of its identity within a Project, not a display
  alias. Do not maintain a second random ID for either entity.
- A GrainKey must encode the domain identity losslessly and consistently, and must decode to the
  same strongly typed identity. Scoped identities use the shared codec; callers must not assemble
  strings such as `projectId:issueNumber` themselves.
- ResourceKey is used for HTTP resource paths and may also be used as the CloudEvents `source`. Do
  not store it as another entity identity in extension attributes, locks, or audit fields.
- An external name may resolve to an identity, but the resolution must not create another entity
  identity.

## Facts, Claims, and Settlement

Uncertainty enters the system only at its edges; the interior is deterministic
and decides from recorded facts alone.

- **Edge**: A point where the system touches what it does not control: a peer process, an external
  runtime, a human-readable message, the clock. Uncertainty is born only here.
- **Fact**: Recorded once, by the one witness present when it became true. Interior state consists
  of facts alone.
- **Witness**: The party present when a fact became true; only the witness records it.
- **Claim**: Anything that crosses an edge asserting a fact but is not one yet. Claims are settled,
  never trusted.
- **Settlement**: The edge action that turns a claim into a fact or an explicit unknown.
- **Unknown**: A first-class value, not a failure. What settlement cannot establish stays an
  explicit unknown.

- Split facts until each has exactly one witness; a dispute means the fact was
  too coarse.
- Settlement is idempotent, records intent before acting, and replays
  outstanding claims after a crash. It belongs to the witness closest to the
  source; there is no central settlement layer.
- An unknown is never rewritten into a convenient fact.
- Facts record decisions; evidence such as logs and diagnostics is never
  state.
- Silence is made decidable by leases: an expiry is a fact, not a guess
  about the peer.
- State travels by propagation: events name what changed, and a consumer's
  copy of another owner's state is either rebuildable or refetchable.

Estimating unrecorded state and fabricating certainty are defects. Existing
instances are known defects; new code must not add them.

## Role suffixes

- `Querier`: single-domain read projection (for example `IssueQuerier`).
- `Assembler`: cross-domain read assembly in AgentOps (for example `AgentActivityFeedAssembler`).
- `Reporter`: cross-domain metrics in AgentOps (for example `AgentUsageReporter`).
- `Resolver`: external name to canonical resource (for example `ProjectResolver`).
- `Manager`: config or lifecycle policy (for example `WorkflowProfileManager`).
- `Store`: persistence boundary for one shape (for example `WorkflowRunStore`).

- No new `*QueryService` names.
- Assembler/Reporter belong to AgentOps. Never in leaf domains like Session.

## ResourceKey

```text literal
/projects/{projectId}
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
/workflow-runs/{workflowRunId}
```

Leading slash. Plural nouns. URL path segments. No trailing slash.

## Entity map

- Project: domain identity and GrainKey `projectId`; ResourceKey `/projects/{projectId}`.
- Issue: domain identity and GrainKey `projectId + issueNumber`; ResourceKey
  `/projects/{projectId}/issues/{issueNumber}`.
- Epic: domain identity and GrainKey `projectId + epicNumber`; ResourceKey
  `/projects/{projectId}/epics/{epicNumber}`.
- WorkflowRun: domain identity and GrainKey `workflowRunId`; ResourceKey
  `/workflow-runs/{workflowRunId}`.
- Runner: domain identity and GrainKey `runnerId`; ResourceKey
  `/projects/{projectId}/runners/{runnerId}`.
- WorkflowBacklog: no own identity; GrainKey `projectId`; ResourceKey
  `/projects/{projectId}/workflow-backlog`.
- StageLock: no own identity; GrainKey is an internal id; ResourceKey
  `/projects/{projectId}/workflow-stage-locks/{resource}`.
- AgentSession: domain identity and GrainKey `sessionId`; ResourceKey
  `/projects/{projectId}/agent-sessions/{sessionId}`.
- Event: domain identity `eventId`; no GrainKey; ResourceKey `/events/{eventId}`.

## External Agent API serialization

The canonical Server/control-plane read models are not an external serialization allowlist. In
particular,
`AgentJobLaunchRead`, `AgentSessionRead`, `SessionInputRead`, `TurnResultRead`,
`TurnDispatchRead`, `BindingTuple`, `LaunchWorkspaceRead`, and
`SessionOperationRead` may contain facts needed by a trusted adapter or
recovery coordinator that a direct caller must never receive.

The direct External Agent API serializes only the explicit public projection in
[`agent-api.md`](agent-api.md). It may expose canonical IDs, public
status/output/error/reason code, timestamps, per-Session public sequence, and
opaque cursor continuation. That projection is a durable Server-owned view fed
by canonical aggregate/outbox facts: its snapshot, journal entries, and source
checkpoint commit together, but it does not make Job and Session cross-aggregate
writes atomic. It must not serialize a binding or operation field,
`runtimeSessionId`, runner/connection identity, request fingerprint, dispatch
attempt/retry/lease/fence state, workspace/workdir/path, prompt/input content,
memory, or raw provider payload. A future public field must be added to the
allowlist in `agent-api.md`; copying it from a canonical schema is not
authorization to expose it.

## AgentSession runtime identity

`sessionId` is Mohist's stable logical AgentSession identity. A runtime-owned physical
Session is identified separately:

Concept ownership and origin rules are defined in
[`agent-execution.md`](agent-execution.md).

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

- Use `runtimeSessionId` for the external physical identity. Never use `acpSessionId` or
  `coderSessionId` as aliases.
- `workflowRunId + sessionName` and `agentId` are origin/lookup references, not AgentSession
  identity. Workflow- and Agent-scoped routes resolve to the canonical `sessionId` resource.
- `runtime` names the execution backend. Do not add a second `kind` field.
- Current runtime binding retains `runnerId`; immutable `workDir` belongs to AgentSession. Together
  they let Session commands survive Runner process restart. A Workflow adapter rejects a request
  whose authoritative workspace differs from the AgentSession workDir; it never silently reuses
  another directory.
- A complete current binding is `(runnerId, runtime, runtimeSessionId, bindingEpoch)`. Every binding
  replacement compares that complete expected binding and the AgentSession workDir. `bindingEpoch`
  is monotonic and changes whenever current binding is replaced; it is part of every command/event
  fence, not a display-only revision.
- A binding operation carries an operation fence. `ownerFence` and `claimGeneration` are
  independent monotonic values; an unqualified `generation` in an implementation means
  `claimGeneration`, never `ContextGeneration`. The single `FenceToken` contract below is used
  by every phase write, candidate create/get/discard/cleanup, binding CAS, completion, Compact,
  and per-target stop. Before any external effect, Server atomically rechecks that token,
  the current owner lease, and the current binding, then passes the same token to Runtime/provider. A stale
  owner fails closed before the effect and before its result is persisted.
- Confirmed-missing recovery stays on the bound Runner and only replaces `runtimeSessionId` while
  incrementing `ContextGeneration` for the new logical context. `rebind` cannot change `runnerId`;
  Runner handoff is an explicit `handoff` operation and is not missing recovery. An adopted candidate
  is current binding and cannot be removed by an old cleanup.
- AgentSession stores only the current binding. It does not expose or persist a physical Session
  history model.
- Compact does not change `runtimeSessionId` or `ContextGeneration`; it persists a ContextBoundary
  and operation result. Reset, runtime change, confirmed missing recovery, or force-reset replaces
  the current binding while preserving `sessionId` and starts a new `ContextGeneration`. A work
  directory change requires a new logical Session identity.

### Canonical AgentSession, launch, and Turn result projections

The canonical Session admission, launch, and Turn result projections are Server-owned read models
whose field lists the code expresses. Only the public projection allowlist in
[`agent-api.md`](agent-api.md) may cross the external serialization boundary.

### Canonical SessionInput and dispatch schema

All launch and follow-up inputs share one input identity contract. The first launch input copies
the caller's `launchRequestId` into `requestId`; a follow-up caller must provide its own stable
`requestId`. Server never invents one after a response is lost. The SessionInput, dispatch, and
retry-work field lists and the dispatch state machine are code-expressed; the lifecycle rules they
implement stay in [`agent-execution.md`](agent-execution.md).

### Canonical effect fence

Every effect carries one complete fence token, summarized under
[AgentSession runtime identity](#agentsession-runtime-identity). The token fields, the match
predicate, and the binding compare-and-swap procedure are code-expressed.

### Canonical SessionOperationRead

Durable Session operations (compact, reset, recovery, force-reset, handoff, rebind, stop, steer)
share one operation read shape keyed by a caller-supplied `operationId`; Server never creates an
unqueryable operation key for those commands.
[`subagents.md#cascade-stop`](subagents.md#cascade-stop) is the sole authority for cascade
membership. The launch identities are separate: the caller provides `launchRequestId`, Server
creates `launchOperationId` exactly once and durably maps `launchRequestId -> launchOperationId`,
and neither identity is a Session operation ID. The field list is code-expressed.

#### Durable steer adapter seam

The Server-to-Runner steer effect uses one stable effect identity, the `(sessionId, operationId)`
pair with `operationId` equal to the caller-provided `requestId`, for apply, query, and replay. A
reused effect identity with a different target, binding, text, or fingerprint is rejected without
calling the provider. The seam shapes are code-expressed.

## WorkflowRun metadata

```text literal
WorkflowRun.Metadata
  ProjectId
  IssueNumber
  EpicNumber?
```

These three values are the Issue context stored locally by WorkflowRun, not a second authority for
Issue or Epic state. Issue provides the current context when it starts WorkflowRun. If affiliation
changes later, a durable event triggers an idempotent command that refreshes `EpicNumber`. Events
produced before the refresh retain the context held by their producer at that time.

Do not add a lineage revision, binding state, or a generic owner/controller reference. On
cross-aggregate redelivery, the handler rereads current Issue state and passes the complete context
to WorkflowRun. An old event therefore cannot write an old affiliation back.

## Dispatch namespaces

Runtime context, Workflow Variables, Project Prompts, and Project Repository resources have
different owners and lifecycles. Do not merge them into one config or Variables document. See
[`workflow/task-dispatch.md`](workflow/task-dispatch.md) for the resolution timing of each
namespace.
