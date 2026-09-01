# Conventions

These conventions define shared identity, fact ownership, naming, serialization,
and execution-fence rules. Owning domain specifications define business
lifecycle and behavior.

## System Boundary

The conventions apply across Server domains, persistence keys, public API
projections, AgentSession bindings, and WorkflowRun metadata. They do not create
a second authority for any domain fact.

## Identity

A domain identity is the smallest permanent, unambiguous key for an entity. A
parent identity and a number within that scope form one identity when the entity
belongs to a parent.

- Project: `ProjectId`, for example `proj_123`.
- Issue: (`ProjectId`, `IssueNumber`), for example (`proj_123`, `42`).
- Epic: (`ProjectId`, `EpicNumber`), for example (`proj_123`, `7`).
- WorkflowRun: `WorkflowRunId`, for example `wr_123`.
- Runner: `RunnerId`, for example `runner_123`.
- AgentSession: `SessionId`, for example `session_123`.
- SessionOperation: `operationId`, for example `op_123`.
- Event: `EventId`, for example `evt_123`.
- Principal: `PrincipalId`, for example `prin_123`.
- Credential: `CredentialId`, for example `cred_123`.

An Issue or Epic number is permanently part of its Project-scoped identity. Do
not maintain another random ID for either entity.

A GrainKey must encode its domain identity losslessly and consistently and must
decode to the same strong identity type. Scoped identities use the shared
codec. Callers must not assemble strings such as `projectId:issueNumber`.

ResourceKey serves HTTP resource paths and may also be a CloudEvents `source`.
Do not store it as another entity identity in extension attributes, locks, or
audit fields. An external name may resolve to an identity but must not create
another identity.

## Facts, Claims, and Settlement

Uncertainty enters only at system edges. Interior state consists of recorded
facts and deterministic decisions.

- **Edge**: contact with an uncontrolled peer process, external Runtime,
  human-readable message, or clock.
- **Fact**: a recorded truth written once by its witness.
- **Witness**: the party present when a fact became true. Only the witness
  records it.
- **Claim**: an edge assertion that is not yet a fact. Claims are settled, never
  trusted. An unqualified Runner `claim` means work acquisition, which
  `ClaimNext` settles synchronously. A Runner fact assertion is a report and is
  settled asynchronously. See [`runner.md`](runner.md#report).
- **Settlement**: the edge action that turns a claim into a fact or explicit
  unknown.
- **Unknown**: a first-class value for what settlement cannot establish.

Split facts until each has exactly one witness. A dispute means the fact was too
coarse. Settlement is idempotent, records intent before acting, and replays
outstanding claims after a crash. It belongs to the witness closest to the
source. There is no central settlement layer.

Never rewrite an unknown into a convenient fact. Facts record decisions; logs
and diagnostics are evidence, not state. Leases make silence decidable: an
expiry is a fact, not a guess about a peer. Events propagate state by naming
what changed. A consumer's copy of another owner's state must be rebuildable or
refetchable.

Estimating unrecorded state and fabricating certainty are defects. Existing
instances are known defects; new code must not add them.

## Naming Roles

Use these suffixes for the stated responsibility:

- `Querier`: single-domain read projection, such as `IssueQuerier`.
- `Assembler`: cross-domain read assembly in AgentOps, such as
  `AgentActivityFeedAssembler`.
- `Reporter`: cross-domain metrics in AgentOps, such as
  `AgentUsageReporter`.
- `Resolver`: external name to canonical resource, such as `ProjectResolver`.
- `Manager`: configuration or lifecycle policy, such as
  `WorkflowProfileManager`.
- `Store`: persistence boundary for one shape, such as `WorkflowRunStore`.

Do not introduce new `*QueryService` names. `Assembler` and `Reporter` belong
to AgentOps and must not appear in leaf domains such as Session.

## ResourceKey

```text literal
/projects/{projectId}
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
/workflow-runs/{workflowRunId}
```

ResourceKeys use a leading slash, plural nouns, URL path segments, and no
trailing slash.

## Entity Map

- Project: identity and GrainKey `projectId`; ResourceKey
  `/projects/{projectId}`.
- Issue: identity and GrainKey `projectId + issueNumber`; ResourceKey
  `/projects/{projectId}/issues/{issueNumber}`.
- Epic: identity and GrainKey `projectId + epicNumber`; ResourceKey
  `/projects/{projectId}/epics/{epicNumber}`.
- WorkflowRun: identity and GrainKey `workflowRunId`; ResourceKey
  `/workflow-runs/{workflowRunId}`.
- Runner: identity and GrainKey `runnerId`; ResourceKey
  `/projects/{projectId}/runners/{runnerId}`.
- WorkflowBacklog: no own identity; GrainKey `projectId`; ResourceKey
  `/projects/{projectId}/workflow-backlog`.
- StageLock: no own identity; GrainKey is internal; ResourceKey
  `/projects/{projectId}/workflow-stage-locks/{resource}`.
- AgentSession: identity and GrainKey `sessionId`; ResourceKey
  `/projects/{projectId}/agent-sessions/{sessionId}`.
- Event: identity `eventId`, no GrainKey; ResourceKey `/events/{eventId}`.

## External Agent API Serialization

Canonical Server read models are not an External Agent API allowlist. These
models may contain facts needed by trusted adapters or recovery coordinators
that a direct caller must never receive:
`AgentJobLaunchRead`, `AgentSessionRead`, `SessionInputRead`, `TurnResultRead`,
`TurnDispatchRead`, `BindingTuple`, `LaunchWorkspaceRead`, and
`SessionOperationRead`.

The direct API serializes only the explicit public projection in
[`agent-api.md`](agent-api.md). It may expose canonical IDs, public
status/output/error/reason code, timestamps, per-Session public sequence, and
opaque cursor continuation. The projection is a durable Server-owned view fed
by canonical aggregate and outbox facts. Its snapshot, journal entries, and
source checkpoint commit together, but Job and Session cross-aggregate writes
are not atomic.

The projection must not serialize binding or operation fields,
`runtimeSessionId`, Runner or Connection identity, request fingerprint,
dispatch attempt, retry, lease, fence, Workspace, workdir, path, prompt or
input content, memory, or raw provider payload. Add a future public field to the
allowlist in `agent-api.md`; copying a canonical schema does not authorize it.

## AgentSession Runtime Identity

`sessionId` is Mohist's stable logical AgentSession identity. A Runtime-owned
physical Session has a separate identity:

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

Use `runtimeSessionId` for the external physical identity. Never use
`acpSessionId` or `coderSessionId` as aliases.

`workflowRunId + sessionName` and `agentId` are origin or lookup references,
not AgentSession identity. Workflow and Agent routes resolve to the canonical
`sessionId` resource. `runtime` names the execution backend. Do not add a
second `kind` field.

The current binding retains `runnerId`; immutable `workDir` belongs to
AgentSession. Together they let Session commands survive Runner restart. A
Workflow adapter rejects a request whose authoritative Workspace differs from
AgentSession `workDir`; it never silently reuses another directory.

A complete current binding is
`(runnerId, runtime, runtimeSessionId, bindingEpoch)`. Every replacement
compares that complete expected binding and the AgentSession `workDir`.
`bindingEpoch` is monotonic, changes whenever the binding is replaced, and is
part of every command and event fence.

A binding operation carries an operation fence. `ownerFence` and
`claimGeneration` are independent monotonic values. Within binding fences, an
unqualified `generation` means `claimGeneration`, never `ContextGeneration`.
Runner-side generations have explicit names: `processGeneration` and the
readiness signal's `runtimeGeneration`. See [`runner.md`](runner.md).

The single `FenceToken` contract applies to every phase write, candidate
create/get/discard/cleanup, binding CAS, completion, Compact, and per-target
stop. Before an external effect, Server atomically rechecks the token, current
owner lease, and current binding, then passes the same token to Runtime or
provider. A stale owner fails closed before the effect and before its result is
persisted.

Confirmed-missing recovery stays on the bound Runner. It replaces only
`runtimeSessionId` and increments `ContextGeneration` for the new logical
context. `rebind` cannot change `runnerId`; Runner handoff is an explicit
`handoff` operation. An adopted candidate becomes current binding and cannot be
removed by old cleanup.

AgentSession stores only the current binding. It does not expose or persist a
physical Session history model. Compact does not change `runtimeSessionId` or
`ContextGeneration`; it persists a ContextBoundary and operation result. Reset,
runtime change, confirmed-missing recovery, and force-reset replace the current
binding while preserving `sessionId` and starting a new `ContextGeneration`. A
work directory change requires a new logical Session identity.

### Canonical AgentSession, Launch, and Turn Result Projections

Canonical Session admission, launch, and Turn result projections are
Server-owned read models whose field lists are code-expressed. Only the public
projection allowlist in [`agent-api.md`](agent-api.md) crosses the External
Agent API boundary.

### Canonical SessionInput and Dispatch Schema

Launch and follow-up inputs share one identity contract. The first launch input
copies the caller's `launchRequestId` into `requestId`. A follow-up caller must
provide its own stable `requestId`. Server never invents one after a response is
lost. SessionInput, dispatch, and retry-work field lists and the dispatch state
machine are code-expressed. Their lifecycle rules stay in
[`agent-execution.md`](agent-execution.md).

### Canonical Effect Fence

Every effect carries one complete fence token, as summarized under AgentSession
Runtime Identity. Code expresses the token fields, match predicate, and binding
compare-and-swap procedure.

### Canonical SessionOperationRead

Durable Session operations, including compact, reset, recovery, force-reset,
handoff, rebind, stop, and steer, share one read shape keyed by the
caller-supplied `operationId`. Server never creates an unqueryable operation key.
[`subagents.md#cascade-stop`](subagents.md#cascade-stop) is the sole authority
for cascade membership.

Launch identities are separate. The caller provides `launchRequestId`; Server
creates `launchOperationId` once and durably maps
`launchRequestId -> launchOperationId`. Neither identity is a Session operation
ID. The field list is code-expressed.

#### Durable Steer Adapter Seam

The Server-to-Runner steer effect uses one stable effect identity: the
`(sessionId, operationId)` pair, with `operationId` equal to caller-provided
`requestId`, for apply, query, and replay. Reusing that identity with a
different target, binding, text, or fingerprint is rejected without calling the
provider. Seam shapes are code-expressed.

## WorkflowRun Metadata

```text literal
WorkflowRun.Metadata
  ProjectId
  IssueNumber
  EpicNumber?
```

These values are Issue context stored locally by WorkflowRun, not another Issue
or Epic authority. Issue provides current context when it starts WorkflowRun.
If affiliation changes, a durable event triggers an idempotent command that
refreshes `EpicNumber`. Events produced before refresh retain their producer's
context.

Do not add a lineage revision, binding state, or generic owner/controller
reference. On cross-aggregate redelivery, the handler rereads current Issue
state and passes the complete context to WorkflowRun. An old event cannot write
old affiliation back.

## Dispatch Namespaces

Runtime context, Workflow Variables, Project Prompts, and Project Repository
resources have different owners and lifecycles. Do not merge them into one
configuration or Variables document. See
[`workflow/task-dispatch.md`](workflow/task-dispatch.md) for resolution timing.

## Non-Goals

- This document does not define business lifecycle, event payload schemas, or
  transport routes.
- A ResourceKey, read projection, or consumer copy does not become a second
  domain identity or fact authority.
- Evidence cannot replace a recorded fact, and an unknown cannot become a
  convenient fact.

## Status

These identity, settlement, naming, serialization, binding, metadata, and
namespace conventions are the current shared design contracts. Field lists and
wire shapes marked code-expressed remain owned by their implementations.
