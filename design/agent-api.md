---
status: wip
---

# Agent API

The Agent API is the common invocation boundary through which Web, CLI, and Agent Connections use
a Mohist Agent. It ensures that an Agent works independently first, then appears through every
entry point with the same identity and behavior.

Domain objects and lifecycles are defined in [`agent-execution.md`](agent-execution.md). This
document records only invocation boundaries and durable design decisions. It does not prescribe a
transport protocol, storage layout, or client SDK.

## Core decisions

| Decision | Conclusion | Reason |
|---|---|---|
| Does an Agent depend on Slack? | No | Web, CLI, and future clients must use an already configured Agent directly |
| Do entry points have different execution semantics? | No | Launch, follow-up, observation, and stop must address the same work and session objects |
| Who supplies Agent configuration? | The Mohist Agent | A client supplies only this task and its context; it cannot override Instructions, Runtime, Model, or Skills |
| Do work and conversation share one lifecycle? | No | AgentJob represents one launch; AgentSession can continue after the initial work completes |
| Who decides state? | Mohist Server | Clients and adapters present state; they do not infer outcomes from logs or provider events |
| Does invocation synchronously await completion? | No | Acceptance, queueing, execution, and result are separate facts; slow work cannot hold a chat or command request open |
| Can a retry create duplicate work? | It must not | Retrying the same intent must return to the original work or input |
| Can accepted input be displaced by new input? | No | Insufficient capacity must reject or queue; it must never silently discard an accepted user delegation |

```text diagram
Web              \
CLI               +--> Agent API --> Agent / AgentJob / AgentSession --> Runner
Agent Connection /
```

The Agent API is an application boundary, not a new domain. It composes Agent, work, and session
capabilities without owning another Agent configuration, work state, or transcript.

## Invocation model

The Agent API provides clients with six capabilities:

| Capability | User intent |
|---|---|
| Discover | Inspect Agent identity, purpose, configuration completeness, and current availability |
| Launch | Create a new AgentJob and AgentSession for one explicit task |
| Observe | Read authoritative work state, session activity, responses, and resumable progress |
| Continue | Submit a follow-up to an existing AgentSession without creating a new AgentJob |
| Control | Stop current execution when authorized, or manage Session context |
| Attach | Provide user-selected files as part of this input |

Launching an Agent creates one work item and one session. The first input and its execution belong
to that AgentJob. The AgentSession can continue after the initial execution ends. A follow-up adds
session input and subsequent execution; it does not reopen the AgentJob.

Follow-up first selects canonical `turnRelation`: a current `running` Turn with an explicitly
steer-capable current binding and no active operation uses `steer`, and the Session transaction
persists one `SessionInput` against that existing `turnId` only. It does not create a second Turn,
dispatch attempt or queue entry, and `queuedTurnCount` does not reject it. If steer is unsupported
or the current Turn is not running, the request uses `new-turn` and only that path checks the Session
queue limit and creates a Turn plus dispatch record. An `outcome_pending`, `unknown` or unresolved
operation is rejected with the original Turn's query next action; unsupported steer alone is not a
rejection when a new Turn can safely queue.

Every follow-up must include a stable caller-provided `requestId`. It is neither a Server-generated
InputId nor an optional client trace id. A unique `(sessionId, requestId)` constraint persists the
request-to-Input/Turn mapping. Response loss or duplicate submission with the same key returns the
same `InputId`, `TurnId`, and canonical state without creating another record set or dispatch. The
same key with different input returns `rejected(idempotency_key_reused)`. Only a different key
represents a new Input and repeats safety admission and queue-limit checks. The initial launch input
uses its `launchRequestId` as `requestId`; the launch operation mapping remains separate.

When queue capacity is full, the Session persists a fingerprint tombstone in the same request map
before returning `rejected(queue_full)`: `inputId=null`, `turnId=null`, and stable reason/nextAction.
A response-loss retry with the same requestId and fingerprint always returns that rejection. A
changed payload always returns `rejected(idempotency_key_reused)` and cannot be accepted under the
same key when capacity later becomes available. The caller can retry only with a new requestId.

Therefore:

- AgentJob completion does not close the whole conversation or prove that a natural-language
  objective is complete.
- AgentSession does not own the business lifecycle of an Issue or Workflow.
- Work that needs continued progress and acceptance still belongs in an Issue / Workflow.
- Web, CLI, and Slack must interpret these states consistently and cannot invent separate meanings
  for "complete."

## Session observation

The stable AgentSession `Session ID` is the sole identity for observation and continuation across
entry points. Project is the read boundary. When callers read by Project and Session ID, sessions
created by a Workflow, direct launch, or Agent Connection use the same summary and transcript
semantics.

- A view and transcript do not switch read models because a session came from an Agent Connection.
  They show the same Session source, Agent identity, current Runtime and activity, inputs, Turns,
  and transcript.
- When discovering sessions by Agent, `--agent` covers both direct launches and Agent Connection
  sessions for that Agent. It is not a history filter limited to manual launches.
- When a Session ID exists in another Project or carries an unsupported source, the read behaves
  as "not found" and does not disclose session facts.

## Execution definition and invocation context

At launch, Mohist resolves and pins the execution definition from the Agent for this Session.
Editing the Agent later does not silently change an existing Session; a new launch uses the latest
configuration. Mohist applies Agent concurrency and scheduling policy uniformly, and no entry point
can bypass it.

The caller may provide:

- current task text or explicit attachments;
- task-related Mohist references such as Issue, Epic, or Repository;
- bounded external discussion context required for the initial launch;
- source and initiator identity used for audit and result delivery.

The caller cannot provide:

- replacements for the Agent's Instructions, Runtime, Model, Variant, or Skills;
- a Runner, working directory, or physical Runtime Session selection;
- chat-platform metadata disguised as system instructions;
- a hidden prompt generated only to pass validation and never shown to the user.

Subagent spawn is the one launch form whose caller is an AgentSession. Its caller Session ID and
idempotency key are explicit, while Server inherits both the authoritative workDir and current
Runner binding from that caller; clients cannot provide a substitute path or Runner. The authoritative
contract is [`subagents.md`](subagents.md).

An input must contain visible text or at least one available attachment. Attachment-only input is
valid. An ordinary URL remains text; whether to visit it depends on the Agent's existing
capabilities. The Agent API does not fetch arbitrary links for the client.

External discussion is imported as background only on the initial launch. The client must state
what it read. If completeness matters to the delegation and context cannot be retrieved reliably,
the client must reject the launch instead of silently submitting incomplete background.

## State boundaries

The API must present these facts separately:

| Fact | Question answered |
|---|---|
| Agent Readiness | Is the Agent configuration known to be executable, known to be incomplete, or temporarily unconfirmed? |
| Agent Availability | Is a Runner and capacity currently available to begin execution? |
| AgentJob status | Is this launch preparing, queued, running, completed, rejected, failed, or cancelled? |
| Session activity | Is this session currently processing input? |
| Input acceptance | Has Mohist durably accepted this user input? |
| Turn result | What are the Runtime execution result and dispatch state for this input turn? |

These facts cannot collapse into one `Connected`, `Running`, or `Success` value. For example, a
Connection can be healthy while its Agent still needs configuration; an Agent can be known ready
but temporarily lack capacity; a failed Slack reply cannot change a completed AgentJob to failed.

`Unknown` is a first-class state, not Ready or Failed. When Mohist cannot determine whether input
reached the Runtime, it must reconcile the original input instead of duplicating it as a defensive
retry.

`outcome_pending` means Mohist knows the input and dispatch path were accepted but has not recorded
the final result. It is not success, failure, or idle, and new input cannot bypass it.

`cancel` and `stop` are deliberately different controls. Cancel deterministically cancels one
identified queued Turn without contacting Runtime. `mo session stop` starts a durable cascade whose
scope is the root Session and its attached subtree; [`subagents.md`](subagents.md#cascade-stop) is
the authority for membership and operation results. Every executing target still uses a complete
fence around the Runtime effect. An unconfirmed result remains `unknown` and cannot be presented as
idle or cancelled.

Availability answers whether a new execution can start now; it does not replace scheduling state
on an existing AgentJob. If a Runner or capacity recovers during backoff for a Pending Job,
Availability may say that a launch can start while that Job still says it is waiting for dispatch.
The Job remains waiting until the next durable dispatch retry actually starts it. Clients must
present both Server conclusions and cannot misreport scheduling wait as Runner offline or capacity
full.

## Canonical read model

The Server is the sole canonical read model for Job, Session, Input, and Turn. CLI, Web, and Agent
Connection adapt this model; they cannot rederive state from local logs, HTTP status, Runner
events, or provider responses. Every model includes the Server `revision` and `observedAt`.

The field and state vocabularies have one authority:

| Concern | Canonical contract |
|---|---|
| Session admission, launch mapping, and Turn result | [AgentSession, launch, and Turn projections](conventions.md#canonical-agentsession-launch-and-turn-result-projections) |
| Input acceptance and dispatch | [SessionInput and dispatch schema](conventions.md#canonical-sessioninput-and-dispatch-schema) |
| Runtime effect fencing | [Effect fence](conventions.md#canonical-effect-fence) |
| Session control operations | [SessionOperationRead](conventions.md#canonical-sessionoperationread) |
| Steer delivery | [Durable steer adapter seam](conventions.md#durable-steer-adapter-seam) |

This document does not repeat those fields or their transaction algorithms. It fixes the
caller-visible invariants that every entry point must preserve:

```text diagram
caller intent + stable key
            |
            v
Server canonical operation ---- confirmed ----> canonical state + nextAction
            |
            +---- unconfirmed ----> unknown
                                      |
                                      +---- query or retry the same key
```

- An accepted launch exposes its Session, first Input, first Turn, resolved workspace, and target as
  live mappings. Before acceptance, after rejection, or while resolution is unknown, absent values
  carry reasons; reservation IDs never masquerade as live resources.
- Once an Input is accepted, later dispatch refusal, backpressure, restart, or response loss cannot
  revoke it or change its Input/Turn identity.
- A retry with the same key and payload returns the original mapping and current canonical state.
  Reusing that key with a different payload is rejected; generating a new key represents a new
  intent.
- `unknown` fails closed. The Server preserves the original operation and effect identity, and the
  client queries or retries that identity. It never infers success from provider output, treats
  unknown as idle, or creates replacement work for safety.
- Every Runtime effect carries the complete canonical fence. The Server rechecks that fence before
  and after the effect; a stale owner, binding, Turn, or context cannot publish a result or recreate
  retry work.
- A retryable dispatch refusal remains a queued Turn and is distinct from terminal
  `outcome=blocked`. An unconfirmed attempt remains `unknown`; exhaustion alone cannot manufacture
  a determinate result.
- Turn outcome remains distinct from dispatch detail. Only a completed Turn may expose a Runtime
  result; failed, cancelled, or blocked outcomes retain actionable reason and nextAction instead of
  a stale result payload.
- A steer follow-up uses its caller `requestId` as the durable effect identity. It preserves the
  accepted Input and existing Turn, creates no second Turn or dispatch lifecycle, and can report
  acceptance only after the Runtime effect is confirmed.
- Clients may render `reason` and `nextAction` for their interface, but cannot merge Job, Session,
  Input, Turn, dispatch, or operation states or remove the actionable meaning.

Compact, Reset, recovery, handoff, rebind, and force-reset require a caller-provided
`operationId`; steer uses its caller-provided `requestId`. A missing key is rejected before durable
state or an external effect is created. Query and response-loss retry reuse the same key and return
the complete canonical operation projection, not a client-specific subset. Launch and cascade stop
use the identities defined in their sections below.

## Launch response loss and force-reset

A launch call requires client-provided `launchRequestId` and `launchRequestFingerprint`, the
canonical hash of the complete request envelope. The key is scoped to Project and Agent. The same
key and fingerprint return the original operation; the same key with a changed request returns
`idempotency_key_reused`; only a new key expresses a new launch intent.

The first accepted request creates one durable `launchOperationId` and one Job. The caller does not
need to know that Server-generated operation ID in advance. After response loss, it locates the
operation by `launchRequestId`, then queries or retries that original operation. A retry can recover
the original rejection or live Session/Input/Turn mappings, but cannot create a second launch.

Session accept/reject is a durable outcome. Explicit rejection or unrecoverable failure returns a
terminal Job with stable `reason` and `nextAction` and no live Session mapping. Successful acceptance
exposes the Session, Input, and Turn together. Temporary unavailability or an unconfirmed side effect
returns `unknown`; pending mapping publication leaves mappings null with reason `mapping_pending`.
Both keep the same operation queryable. Reservation identities never fill live mapping fields.

When Unknown or operation response loss blocks ordinary Compact, Reset, or recovery, the client
first queries the original operation using its caller-provided `operationId`. Force-reset is the
explicit product action for moving past Unknown:

```text diagram
unknown
  |
  +--> query original operation --> determinate --> continue
  |
  +--> still unknown + explicit confirmation
         --> force-reset with a new operationId
               --> new context becomes current
               --> old effects remain visible as unresolvedPrevious
```

A force-reset request must use a new operationId and explicitly confirm that the old Runtime may
still have side effects and the old result remains unknown. It also supplies the current
`expectedRevision`, `expectedContextGeneration`, and complete expected binding. These values make
the risk acknowledgement specific to the state the caller inspected rather than a reusable
confirmation for any later Session state.

The following guarantees hold:

- Reusing the same operationId and identical request returns the original operation. A changed
  fingerprint, revision, generation, or binding returns `idempotency_key_reused` or
  `operation_payload_mismatch` and cannot create a second context.
- New Input/Turn admission remains blocked until the replacement binding and context boundary are
  confirmed. Response loss keeps the same operation queryable; retry cannot increment the context
  generation again.
- Every unknown operation, Input, Turn, dispatch attempt, and Runtime effect from the old generation
  remains recorded in `unresolvedPrevious`. Force-reset supersedes their authority; it does not
  relabel them as successful or failed.
- A replacement binding is adopted only when its identity is complete, matches the requested
  Runner and Runtime, and advances the expected binding epoch. An absent, mismatched, incomplete,
  or unconfirmed candidate fails closed and cannot authorize a binding swap.
- Candidate creation, binding replacement, cleanup, and completion all use the complete canonical
  fence. A stale pre-effect or post-effect result cannot become current or delete a newer binding.

The exact operation projection, candidate states, and fencing algorithm remain authoritative in
[`conventions.md`](conventions.md#canonical-sessionoperationread).

The corresponding CLI contract is:

```bash
mo session force-reset <session-id> \
  --operation-id <new-operation-key> \
  --expected-revision <revision> \
  --expected-context-generation <generation> \
  --expected-binding <binding> \
  --confirm-unknown-side-effects
```

The CLI first shows the current Session, pending Input/Turn, Unknown state from the old operation,
and a risk summary. The Server rejects a call without explicit confirmation, operationId, and the
current revision, or when the Session has changed. After force-reset completes, CLI/Web treats only
the new context's activity as current while showing the count and nextAction for old unresolved
facts.

`handoff` and `rebind` follow the same command/query contract. Only handoff can change Runner;
rebind can replace the Runtime binding only on the same Runner. Runner reconnect, timeout, or a
stale event cannot implicitly trigger either operation. Both require `operationId`, current
revision, expected binding, and a bounded deadline, and are admitted only while the current context
is idle with no pending side effect. Active, outcome-pending, or unknown work must be queried or
force-reset first. A successful boundary creates a new context generation, so facts from the old
binding cannot overwrite it.

Runtime disconnect, timeout, unavailability, and ambiguous responses retain the current binding and
remain `unknown`. Automatic recovery is allowed only after the same Runner confirms that the Runtime
Session is definitely missing, the current context is idle, no unknown side effect exists, and
Session admission is ready. Otherwise the API permits observation and explicit reconciliation only;
it never treats transport failure as proof that a new binding is safe.

A response-loss query/retry returns the same `operationId` and complete conventions projection,
not partial confirmation containing only state or mappings. An operation with `outcome=blocked` is
terminal and the caller must perform its `nextAction`; client polling cannot keep it pending
indefinitely.

### Stop operation

`cancel` addresses one explicitly selected queued Turn and never contacts Runtime. It is not the
running-work control.

`mo session stop <session-id> --idempotency-key <key>` instead creates a durable cascade rooted at
that Session. The Server derives the operation ID from the root and key, then freezes membership
from the authoritative attached-subtree snapshot. Callers cannot supply a Turn list, graph revision,
or binding. Membership, detach races, retry, and aggregate results are defined only by
[`subagents.md`](subagents.md#cascade-stop).

The Agent API preserves these per-target guarantees inside that cascade:

- A queued Turn is cancelled without contacting Runner. An executing Turn is stopped only through
  its expected Runner and binding.
- Every Runtime stop is guarded by the complete [effect fence](conventions.md#canonical-effect-fence)
  before and after the call. If the Turn or binding changed, the stale operation cannot affect the
  replacement work.
- If Runtime may have acted but its response is unconfirmed, the target remains `unknown`. Query or
  bounded retry reuses the same target operation and provider identity; it does not create another
  stop identity, act on later work, or report idle/cancelled.
- The cascade acts only on its frozen snapshot. A later Turn is outside that operation, and no
  AgentSession becomes terminal. Control of the initial Turn may affect its AgentJob; controlling a
  later Turn never rewrites an already terminal Job.

## Reliability contract

All clients share these guarantees:

- Retrying the same invocation intent after timeout, disconnect, or restart still addresses the
  original work or input.
- Once input is confirmed accepted, process restart, queue pressure, or new messages cannot make
  it disappear.
- A client can resume observation from a known position without relying on an uninterrupted
  long-lived connection.
- Queueing and backpressure are visible states, not disguised execution failures.
- Mohist durably retains terminal state and transcript; provider delivery state cannot overwrite
  them.
- When an external platform offers only at-least-once delivery, the Connection deduplicates it.
  The Agent API does not assume that a platform sends an event only once.
- Launch response loss, Session acceptance rejection, and unrecoverable failure first locate the
  corresponding `launchOperationId` through the original `launchRequestId`, then return the
  durable outcome. They leave neither an executable dangling reservation nor a permanently
  pending Job.

The guarantee is "one Mohist domain effect for one intent," not exactly-once delivery over the
network. When a request result cannot be confirmed, the client queries or retries with the same
identity and cannot generate a new invocation identity.

Queues must be bounded, but exact capacity is an operating parameter rather than part of the
product model. At the boundary, reject new input with actionable feedback. Never discard the
oldest accepted input.

## Identity and authorization

The Agent API distinguishes two caller classes:

- A Mohist operator uses an Agent directly through Web or CLI.
- An Agent Connection invokes one fixed Agent on behalf of a member verified by an external
  platform.

An external member identity is not a Mohist administrator identity. The provider adapter first
enters a trusted Server Connection boundary. That boundary checks workspace, member, and access
policy for the corresponding Connection before invoking the Agent API. It has the permissions
needed for invocation and observation but cannot edit the Agent, change execution configuration,
or manage another Project.

The first Connection credential is a Mohist-owned service identity, not a general third-party API
key. See [`auth.md`](auth.md) for control-plane authentication and identity. A public developer
platform and multi-tenant authorization remain non-goals and must not be extrapolated from the
Slack adapter permission model.

## Attachment boundary

Files from an external platform enter a Mohist-managed attachment boundary before becoming Agent
input. This gives Web, CLI, and Connections the same input semantics without exposing Slack
credentials or temporary download URLs.

These rules must hold:

- Process only files that the user explicitly attached to the current input or explicitly imported
  into context.
- Make file source, name, type, and availability visible; do not ignore a read failure.
- Keep provider tokens, temporary URLs, and raw event payloads out of Agent configuration and
  transcript.
- An attachment belongs only to the input that accepted it and cannot be reused by reference from
  another caller.
- Mohist applies cleanup, size, and retention policies consistently instead of leaving them to
  each adapter.

## Error principles

Errors first help the caller choose a next action rather than expose internal exceptions. At least
these categories remain distinct:

| Category | Caller action |
|---|---|
| Invalid input | Correct the current task or attachment and submit again |
| Identity or access denied | Use the correct identity, or have the Connection Owner change access policy |
| Agent needs configuration | Fix the Agent in Mohist; an entry point cannot override configuration |
| Temporarily unavailable | Retain the invocation identity and wait or retry |
| Capacity full | Show backpressure explicitly and submit later; accepted input remains unaffected |
| State conflict or unknown result | Reread authoritative state instead of blindly starting new work |

A messaging platform may hide sensitive configuration detail, but it must give the user an honest,
actionable summary. The Owner and Mohist operators can inspect complete diagnostics on the control
plane.

## Decisions informed by Buzz

The Buzz implementation demonstrates that a chat entry point needs an explicit caller access
policy and a bounded queue. Mohist adopts both principles while retaining its own state boundaries:

- Access policy belongs to the Agent Connection, not Agent execution configuration.
- An adapter does not persistently cache platform events. The Server provider inbox decides to
  take ownership or reject them; when the result is unknown, the provider redelivers with the same
  identity.
- Both the Server input queue and provider outbound outbox are bounded, but neither may discard
  content after it becomes a SessionInput.
- Provider conversation mapping and delivery state belong to Server infrastructure. They are not
  the authority for AgentJob, AgentSession, or execution results.

## Non-goals

- The Agent API does not interpret Slack mentions, threads, member directories, or platform rate
  limits.
- The Agent API does not run a Runtime or infer work state from Runner logs.
- The Agent API does not replace Workflow, Issue, or event-routing interfaces.
- The first version does not promise a public developer platform, general OAuth, or cross-
  organization tenant isolation.
- This document does not fix HTTP paths, DTOs, database tables, lease protocols, or SDK versions.

## Status

The current Web UI and CLI have basic paths for creating and launching Agents and viewing and
continuing sessions, but the cross-entry-point contract above is incomplete, especially for input
identity, execution turns, duplicate-request protection, observation after disconnect, and
concurrent scheduling. The named Agent profile now solely owns its execution definition; client
input cannot override it. Skills are pinned for each execution.

### Implementation gap: caller key is required

Target behavior is explicit: every follow-up must carry a non-empty caller-provided `requestId`, and
every Compact, Reset, recovery, handoff, rebind, and force-reset command must carry its
caller-provided `operationId`. Cascade stop instead requires the caller's `Idempotency-Key` and
derives its operation ID as defined in [`subagents.md`](subagents.md#cascade-stop). Missing keys are
rejected before any durable operation, Input, Turn, candidate, or hidden identity is created. A
response-loss retry reuses the original key.

Current routes and Grains still have requestId/operationId-based paths that generate a hidden key
when the caller key is missing. This is a known follow-on implementation gap, not an alternative
contract or a claim that the current implementation is complete. The migration boundary is the
Server admission layer: all affected entry points must pass the caller key into the canonical
admission/operation path, which rejects null or empty keys before any state or external effect.
Until that work lands, this target design remains the authority for new implementation and the gap
must remain visible in delivery status.

The current Server stop route also still accepts a legacy request body containing `turnId` and
executes the pre-cascade single-Turn stop path. That body is not part of the target API and is not a
compatibility contract. It must be removed so the route accepts only the bodyless cascade request
with `Idempotency-Key` defined in [`subagents.md`](subagents.md#cascade-stop).

Product dependencies determine implementation order:

1. Make the Agent API fully express launch, observation, continuation, stop, and attachment input
   in Web and CLI.
2. Move every direct entry point to the same state and reliability semantics, proving that an
   Agent works without Slack.
3. Finally, attach Slack Connection as an ordinary client without adding capabilities through
   shell execution, log parsing, or hidden configuration.

See [`slack.md`](slack.md) for Slack identity, access, thread routing, and delivery design.
