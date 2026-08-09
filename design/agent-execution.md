# Agent Execution Model

This document defines why Workflow work, Agent work, logical Sessions, physical Runtime Sessions,
and Runtime adapters have separate owners. Runtime-specific behavior belongs in
[`runtimes/`](runtimes/README.md). Canonical internal read schemas and the complete fencing
protocol belong in [`conventions.md`](conventions.md). Authentication, transport, public replay-key
mapping, and external API projections belong in [`agent-api.md`](agent-api.md).

## Design Drivers

Three forces shape the model:

- **Stable identity.** A user follows one logical conversation even when its Runner process or
  physical Runtime Session is replaced. Public Session identity therefore cannot belong to an
  external Runtime.
- **Unknown external effects.** A timeout or lost response does not prove that a Runtime command
  failed. Mohist must preserve `unknown` and reconcile the original identity before retrying.
  Guessing can duplicate input or apply a destructive context operation twice.
- **Cross-owner convergence.** TaskRun or AgentJob decides work lifecycle, while AgentSession owns
  conversation state. They do not share a transaction. Durable request identity and durable
  messages must converge their observations without moving either decision to the other owner.

Exposing a physical Runtime Session as the public Session would avoid a binding layer, but every
replacement would change user identity and leak provider lifecycle. Mohist instead exposes a
stable AgentSession and treats the physical Runtime Session as its replaceable current Binding.
The cost is explicit operation state and fencing; the benefit is one durable conversation identity
across Runtime loss.

## Ownership and Call Paths

| Concept | Owner | Authoritative decisions |
|---|---|---|
| Mohist Agent | Agent context | identity, Instructions, execution configuration, Skills, archival |
| TaskRun | Workflow context | Workflow task lifecycle, result, retry, recovery, advancement |
| AgentJob | Agent context | lifecycle and result of one Agent work item |
| AgentSession | Session context | Input order, Turns, Transcript, Activity, context, usage, current Binding |
| Runtime Session | external Runtime | physical provider Session and execution facts |
| Runtime adapter | Runner process | provider protocol, process resources, event reconciliation, error classification |

There are two work-owner paths:

```text diagram
Workflow TaskRun -- resolved Action ----+
                                       +--> Runtime adapter --> Runtime Session
Agent AgentJob ---- execution snapshot -+
                         |
                         +--> AgentSession records conversation facts
```

TaskRun remains the owner when a Workflow calls `mohist/opencode`, `mohist/pi`, or a resolved
`mohist/agent` definition. Resolving a named Agent for a Workflow task snapshots its Instructions
and execution configuration for that dispatch; it does not create AgentJob or transfer work
lifecycle to the Agent context. Resolution occurs again for each dispatch attempt, so a retry uses
the definition that exists for that attempt. A missing or archived Agent fails dispatch instead of
falling back to another Runtime path.

Web, CLI, Agent Connection, event routing, and mentions are call origins for the AgentJob path.
They all enter through the canonical AgentJob launch boundary and cannot create a third execution
path. A provider adapter such as Slack may translate ingress and delivery, but it cannot snapshot
an Agent, own a Runtime Session, or decide a work result.

Direct API callers cross an additional trust and projection boundary. Bearer PAT authentication
and Project/scope authorization finish before resource or idempotency lookup, admission, durable
write, or external effect. Responses and events expose only the public projection defined in
[`agent-api.md`](agent-api.md); canonical internal models, physical Binding, workspace paths,
prompt or memory content, and Runner control remain private.

An Inline Agent is a usage mode, not another entity. It is a TaskRun that directly selects a
Runtime-specific Action. The Workflow Action adapter and AgentJob executor may reuse the same deep
Runtime module; they must not reuse each other's work lifecycle.

## Work lifecycle and Session

TaskRun and AgentJob own pending, running, and terminal work states; success and failure; and retry
or recovery decisions. AgentSession owns ordered SessionInput and AgentTurn records, Transcript,
context, usage, Activity, and current Runtime Binding.

The Workflow Action adapter reports a work result to TaskRun. The AgentJob executor reports a work
result to AgentJob. Both report conversation facts to AgentSession. A Session event cannot advance
a Workflow or make an AgentJob terminal. A work failure may appear in Transcript, but AgentSession
does not arbitrate that result.

A Follow-up is a Session command, not a new work dispatch. It appends a SessionInput to an existing
AgentSession and either joins the current Turn through steer or creates a later Turn. It does not
create a TaskRun or AgentJob. Compact, Reset, recovery, rebind, handoff, and force-reset also change
only the Session.

AgentJob references the first Input and Turn created by launch. A completed AgentJob means that the
launch work returned successfully. It does not mean the AgentSession is closed or that the
natural-language task is semantically complete. Later Follow-ups never reopen or rewrite the
original AgentJob. Business lifecycle belongs in Issue and Workflow.

Agent launch fixes Instructions, Runtime, Model, ReasoningEffort, Variant, Skills, and Workspace identity for that
Session. Later input uses the same execution snapshot. Policy changes do not rewrite an execution
that has already started. The entry point resolves a named Workspace from its Origin and persists
that identity before acceptance; CLI, Web, and Slack have different Origin rules. The Runner may
materialize the work directory later. A caller can select a Workspace by Name where the entry point
allows an override, but cannot substitute a raw path or Runner default. Workspace resolution and
materialization are authoritative in [`workspace.md`](workspace.md#binding-and-resolution).

### Execution configuration resolution

`reasoningEffort` is a cross-Runtime user setting with exactly these values:
`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, and `max`. `variant` is a
separate Runtime-specific setting and is never an alias for effort. A saved Agent
has one persisted effective Runtime, Model, ReasoningEffort, and Variant tuple.
The only per-launch overrides are Model and ReasoningEffort; they apply to that
Job only.

Each Runtime integration owns a static, versioned capability catalog. A catalog
entry identifies its `catalogVersion`, Runtime, Model, supported and default
ReasoningEffort values, whether a Variant dimension applies, supported Variant
values, nullable `defaultVariant`, and the complete non-secret native mapping
needed by that Runtime. `defaultVariant` is null only when the Model has no
Variant dimension. Server owns the accepted catalog registry and validates Agent
create/edit, readiness, and launch resolution against it. It does not probe a
provider, list live models, or infer support from credentials.
`RuntimeCapabilityCatalogRead.defaultExecution` supplies the complete persisted
tuple when Agent creation omits configuration. It also defines the nullable
Variant rule: an effective Variant is `null` only if that Model has no Variant
dimension; clearing Variant is an operation, never a null value. There is no
legacy path that treats Variant as effort.

The absent, value, clear, and invalid rules are fixed at the Agent boundary. A
clear exists only on Agent edit, via the named `--clear-*` flag (or its typed
internal equivalent). Each execution field is exactly one of `omitted`, `set`,
or `clear`; a clear and a supplied value for the same field are mutually
exclusive. To keep the dependency resolution deterministic, one edit may use
at most one of the four execution clear flags. Values for other fields may be
combined with that one clear.

| Field | Agent create: omitted | Agent edit: omitted | Agent edit: explicit clear | Value and invalid input |
|---|---|---|---|---|
| Runtime | Use the catalog's `defaultExecution.runtime`. | Retain the saved Runtime. | `--clear-runtime` seeds the catalog's complete `defaultExecution` tuple before valid values for other fields are applied. | A supplied Runtime replaces only Runtime; retained dependent fields must validate or the edit is rejected. Empty, `null`, or an unknown option is `invalid_execution_configuration`. |
| Model | Use the selected Runtime's catalog default Model. | Retain the saved Model. | After a supplied Runtime, if any, `--clear-model` selects that Runtime's default Model, ReasoningEffort, and Variant before valid effort/Variant values are applied. | A supplied Model replaces only Model; retained ReasoningEffort and Variant must validate or the edit is rejected. Empty, `null`, or an unknown option is `invalid_execution_configuration`. |
| ReasoningEffort | Use the selected Model's `defaultReasoningEffort`. | Retain the saved ReasoningEffort. | After Runtime and Model selection, `--clear-reasoning-effort` selects that Model's current `defaultReasoningEffort`. | A supplied value must be one of the closed enum and be supported by the selected Model. Empty, `null`, unknown, or unsupported is rejected. |
| Variant | Use the selected Model's `defaultVariant`; it is null only when that Model has no Variant dimension. | Retain the saved Variant. | After Runtime and Model selection, `--clear-variant` selects that Model's current `defaultVariant`. | A supplied value must be a supported Runtime-specific Variant. Empty, `null`, unknown, or unsupported is rejected; a value is illegal when no Variant dimension applies. |

The CLI and typed internal edit surface therefore accept this complete
combination matrix:

| Operation | Omitted fields | Supplied values | Clear flags | Result |
|---|---|---|---|---|
| Agent create | Any subset | Any subset of Runtime, Model, ReasoningEffort, and Variant | None allowed | Omitted fields start from catalog defaults; resolve and persist one complete tuple. |
| Agent edit, no clear | Any subset | Any subset of the four fields | None | Omitted fields retain saved values; validate the final tuple. |
| Agent edit, one clear | Any subset other than the cleared field | Any subset of the other three fields | Exactly one of the four execution clear flags | Apply Runtime, Model, ReasoningEffort, Variant dependency order as described above, then validate and persist the complete tuple. |
| Agent edit, field both set and clear | N/A | The cleared field is also supplied | One clear for that field | Reject `invalid_execution_configuration`; there is no precedence rule. |
| Agent edit, multiple execution clears | N/A | Any | Two or more | Reject `invalid_execution_configuration`; use separate edits. |
| Agent create or launch, any execution clear | N/A | Any | One or more | Reject `invalid_execution_configuration`; clear is an Agent-edit operation only. |

An explicit clear always recalculates Readiness and final native mapping for the
whole candidate tuple. It does not mean that the resulting Variant is null: the
only valid null effective Variant remains a Model with no Variant dimension.

Every create or successful edit resolves, validates, and persists the entire
effective tuple, including its `catalogVersion` and final native mapping. A
clear recomputes Readiness and that complete native mapping. A failed
recalculation is atomically rejected and leaves the previously saved tuple
unchanged. Runtime and Model changes never probe or fall back to a compatible
effort or Variant.

For new work, the following boundary rules use that saved tuple:

| Boundary | Omitted launch configuration | Explicit clear | Other rule |
|---|---|---|---|
| CLI new AgentJob | Use the saved Agent tuple. | Illegal. | `--model` and `--reasoning-effort` are the only explicit one-Job overrides. |
| Workflow Agent-definition execution | Use the saved Agent tuple. | Illegal. | The `mohist/agent` definition reference never obtains an implicit Runtime or Session default. |
| Existing AgentSession Follow-up | Keep the immutable Job/Session snapshot. | Illegal. | A later Agent edit or catalog change never silently re-resolves the Session. |

An empty string, `null`, an unknown property, or a value outside the closed
ReasoningEffort vocabulary is illegal wherever the field is accepted. Variant
remains a separate Runtime-specific field in every row.

A Workflow Agent-definition execution is a TaskRun attempt rather than an
AgentJob, but it follows the same saved-default rule. Inline Runtime Actions are
separate contracts; they do not silently turn a current physical Session into an
Agent default.

Validation has five stable outcomes before dispatch:

| Condition | Result | Action |
|---|---|---|
| An option is unknown, empty, not a string, or has an effort outside the closed vocabulary | `invalid_execution_configuration` | `correct_execution_configuration` |
| A well-formed Runtime or Model is absent from the accepted catalog | `unsupported_execution_configuration` | `select_supported_execution_configuration` |
| A catalog Runtime/Model cannot use the selected ReasoningEffort or Variant combination | `incompatible_execution_configuration` | `select_compatible_execution_configuration` |
| The required static catalog cannot be read, or Readiness is `unknown` | `execution_catalog_unavailable` | `wait_for_catalog` |
| A valid configuration cannot be executed by an available adapter for its persisted catalog version and native mapping | Job remains waiting with `exact_execution_unavailable` | `wait_for_exact_execution` |

The first four outcomes reject every new launch or delegation before a Job,
Session, Workspace, attachment, Runner, dispatch, or provider side effect.
Existing Session history remains readable. A Follow-up may retain its immutable
snapshot, but when its admission sees an unavailable catalog or Unknown
Readiness it rejects before creating a new Input, Turn, or dispatch effect; it
does not re-resolve to a different tuple. The fifth retries the same immutable
configuration when that exact adapter becomes available. It never changes
Runtime, Model, ReasoningEffort, Variant, catalog version, or native mapping as
a fallback.

Resolution starts only for a new launch after structural input validation and
the read-only replay lookup described below. It selects the saved Agent value
unless an allowed launch field is explicitly present, records the source for
each field, selects one catalog entry, and stores the resulting
`ResolvedExecutionRead` in the durable admission claim before any materialized
effect. That immutable snapshot includes `catalogVersion` and the final
Runtime-owned `nativeMapping`; a later Agent edit or catalog release cannot
rewrite, erase, or recompute it. Runner dispatch applies that saved mapping
rather than resolving a new one. Trusted Job launch responses and Job views
return the snapshot; the #387 direct API retains its smaller public projection.
Session shows only a read-only association summary.

The canonical read shapes, gap messages/actions, nullability, and catalog
ownership are in [`conventions.md`](conventions.md#canonical-agentsession-launch-and-turn-result-projections).

### Resolve and commit boundary

`ResolveAgentLaunch` is a pure resolve-and-validate phase. It reads the Agent,
the accepted static catalog, existing Workspace and attachment references, and
the caller's attachment descriptors. The CLI may read a local path only at its
edge to produce a non-path `name`, `byteLength`, and `contentFingerprint`
descriptor; a local path never enters Server, a persisted resource, or a public
DTO. Resolve normalizes valid caller intent, chooses the saved defaults and
allowed overrides through `ResolveExecutionConfiguration`, selects one catalog
entry, and returns `ResolvedExecutionRead`, its
`ExecutionResolutionFenceRead`, and `LaunchMaterializationPlanRead`. It may
derive a missing Project Workspace name or identify a local attachment that
would upload, but it does not allocate an ID, create a claim, upload bytes, or
write any resource.

`CommitAgentLaunch` runs only after a successful resolve for a new real launch.
Its durable `FenceResolvedAgentLaunch` operation is the admission boundary, not
the preceding read. In one compare-and-claim operation it first checks the
`AdmissionScope` for the caller, Project, Agent, and `launchRequestId`. A
matching caller-intent fingerprint returns the existing claim unchanged; a
different caller intent returns `idempotency_key_reused` (409). When there is no
claim, it verifies that the Agent execution revision and catalog entry still
match the pure resolution fence. A changed fence causes a new pure resolution,
with no claim or effect. Only a matching fence can atomically persist either the
`pending` claim with its immutable execution snapshot and full fingerprint, or
a pure rejection tombstone. The full fingerprint includes caller intent and the
complete resolved Runtime, Model, ReasoningEffort, Variant, source,
`catalogVersion`, and native mapping.

Only the resulting claim's `launchOperationId` may materialize every
`wouldCreate` Workspace and `wouldUpload` attachment, create the
Job/Session/Input/Turn, and request dispatch. The real launch path shares
exactly the same resolve phase as dry-run; commit is the only phase allowed to
create a claim or cause those effects.

Dry-run accepts only the parse, authorization, caller-intent validation, and
pure resolve phase. It returns `AgentLaunchPreviewRead`; it creates no
Workspace, attachment, admission claim, idempotency record, Job, Session,
Input, Turn, reserved identity, dispatch, Runner work directory, or provider
effect. Existing references are read-only resolved. A missing-but-derivable
Workspace or local attachment is returned only as `wouldCreate` or
`wouldUpload`; a missing, unreadable, ambiguous, over-limit, or invalid input
rejects with the structured
`LaunchResolutionProblemRead` or execution error. `--dry-run` and an
idempotency key are mutually exclusive, because preview does not create, read,
or wait on an admission claim or idempotency record.

### Launch convergence

AgentJob and AgentSession have separate write authorities, so launch is not one
cross-aggregate transaction. The durable protocol instead makes every partial
state queryable:

```text diagram
caller launchRequestId + canonical caller intent
          |
          v
pure ResolveAgentLaunch -> resolved execution + resolution fence
          |
          v
FenceResolvedAgentLaunch (atomic scope lookup + fence compare + claim)
          |
          +--> pre-materialization tombstone
          |
          +--> pending effect rows: Workspace -> attachment[n] -> Job -> Session/Input/Turn -> dispatch
          |                              |                  |        |
          |                              +------------------+--------+--> post-materialization rejection
          |
          +--> committed | uncertain recovery
```

- The caller supplies a stable `launchRequestId`. Server authenticates and
  authorizes first, validates the keyed envelope, then normalizes canonical
  caller intent; a caller-supplied fingerprint is never trusted. Intent contains
  task, allowed context and Workspace references, attachment identities or
  non-path attachment descriptors, and explicit presence/value of each allowed
  per-launch override. It never contains a local path.
- The Server looks up the canonical `AdmissionScope`
  `(callerId, projectId, agent-launch, agentId, launchRequestId)` before a new
  admission. An existing scope compares the stored caller-intent fingerprint:
  a mismatch is `idempotency_key_reused` (409), while a match returns the
  original claim without resolving current defaults or catalog metadata. Its
  stored `launchRequestFingerprint` nonetheless contains the first admission's
  caller intent and complete resolved execution snapshot, including tuple,
  `catalogVersion`, native mapping, and sources.
- With no existing claim, Server runs `ResolveAgentLaunch`. Its result is only a
  draft until `FenceResolvedAgentLaunch` atomically rechecks its Agent execution
  revision and catalog-entry fingerprint. A stale draft is discarded and
  re-resolved without a claim. A matching fence atomically writes either a
  `pending` claim or a pure rejection tombstone. Concurrent callers may both
  resolve, but only one matching compare-and-claim wins; the other rereads that
  winner and never materializes a second set of effects.
- After a valid keyed envelope has crossed authentication and authorization,
  every pure validation, readiness, catalog, Workspace, attachment, or Session
  admission rejection is a durable tombstone. Same-key replay returns that
  result after later recovery; only malformed envelopes, failed authentication,
  failed authorization, or invalid/missing keys occur before this guarantee.
- A `pending` claim first persists a durable effect row for its Workspace, each
  attachment, and Job. Each row has an operation-owned identity lookup and a
  recovery fence. The claim owner may transition only its own effect from
  `planned` to `materializing` to `materialized`; it then creates the
  Job/Session/Input/Turn and requests dispatch under the same operation identity.
  `committed` is published only with real accepted IDs and the immutable snapshot.
- A definite failure before any effect begins is a
  `pre-materialization-tombstone`. A definite failure after an effect begins is
  a `post-materialization-rejection`: each started effect records its durable
  identity and finishes deterministic compensation when safe, otherwise remains
  terminally rejected with that identity. An indeterminate effect result is
  `uncertain`; recovery first uses its operation-owned lookup and recovery fence,
  then finishes or compensates only that effect. It cannot mint a second claim,
  ID, Workspace, attachment, or Job.
- Before an attachment is marked materialized, the transfer rechecks the
  claim's frozen descriptor against the actual bytes. A different length or
  content fingerprint is `attachment_content_changed`. It becomes the same
  claim's pre-materialization tombstone when no effect has started, or its
  post-materialization rejection when another effect already started. Replaying
  the key returns that result; resubmitting changed bytes under the key conflicts
  because the descriptor is caller intent, and a new key is required.
- No Workspace, attachment, Job, Session, Input, Turn, dispatch, Runner, or
  provider effect occurs before the durable claim. The protocol owns and
  reconciles each effect independently; it does not claim a cross-aggregate
  transaction.

The #434 CLI adapter follows this same caller-intent, full snapshot fingerprint,
and compare-and-claim rule for its Model and ReasoningEffort flags. The #387
direct API follows the same scope and replay invariant, but its current public
launch body has no execution overrides, attachments, or context references. A
direct API override can enter caller intent only after
[`agent-api.md`](agent-api.md) explicitly adds that field to the public schema;
an adapter must not infer or silently accept one.

The launch projection and null rules are authoritative in
[`conventions.md`](conventions.md#canonical-agentsession-launch-and-turn-result-projections).

### AgentSession invariants

```text diagram
AgentSession (stable logical identity)
  | owns in order
  +--> SessionInput -- belongs to exactly one --> AgentTurn
  +--> Transcript facts
  +--> current ContextGeneration
  +--> CurrentBinding --> one physical Runtime Session
  +--> at most one ActiveOperation
```

The invariants are:

- `Id`, `Source`, and `WorkDir` do not change during the Session lifecycle.
- Optional parentage is a child-owned `SessionParentLink`, separate from immutable `Source`. The
  tree contract is authoritative in [`subagents.md`](subagents.md).
- `CurrentBinding` is one complete `(runnerId, runtime, runtimeSessionId, bindingEpoch)` tuple.
  Replacement changes it atomically and monotonically; AgentSession stores no physical Session
  history.
- A new Session starts at `ContextGeneration=1`. Its Binding may be null before the first execution
  establishes a physical Runtime Session; initial dispatch starts only after Binding and Session
  admission have both committed.
- Compact keeps the current generation. Reset, Runtime change, confirmed-missing recovery,
  force-reset, handoff, and rebind increment it only with their committed Context boundary.
- One AgentSession has at most one Runtime execution at a time. Transcript order is therefore
  sufficient for the conversation.
- Each accepted Input has one stable Input ID, caller `requestId`, fingerprint, Turn ID, and
  `ContextGeneration`. It never moves to another Turn or generation.
- A Turn can own multiple steer Inputs, but a new-turn Input creates a distinct Turn.
- Capacity rejection occurs before acceptance. Once accepted, an Input cannot be discarded,
  overwritten, or assigned a replacement ID.
- User input contains visible text or an explicit attachment. Attachment-only input does not gain
  a hidden prompt.
- AgentSession has no `completed`, `failed`, `stopped`, or `closed` lifecycle.
- An ActiveOperation cannot be cleared merely because its owner disappeared or a response was
  lost. It remains queryable until it reaches a definite terminal result or is explicitly
  superseded under the canonical operation contract.

The persisted domain records may contain write-side data needed to enforce these invariants, but
they do not define another schema. Canonical internal Session, Input, Turn, dispatch, operation,
and fence fields are defined only in [`conventions.md`](conventions.md).

### Operation identity

Durable identity is fixed at ingress before any external effect. Trusted callers provide the
canonical command identity directly; an external adapter durably maps its caller-held public key
to that identity before invoking the command.

| Intent | Ingress replay identity | Durable operation identity |
|---|---|---|
| Launch | `AdmissionScope(callerId, projectId, agent-launch, agentId, launchRequestId)` | Server creates and durably maps one `launchOperationId` |
| Follow-up with new Turn | `AdmissionScope(callerId, projectId, session-followup, sessionId, requestId)` | the same request map identity |
| Steer | caller `requestId` | the same `SessionOperationRead.operationId` |
| Compact, Reset, recovery, rebind, handoff, force-reset | caller `operationId` | the same operation ID |
| Direct API Turn stop | caller `Idempotency-Key` | adapter maps one private operation ID for the frozen Turn |
| Cascade stop | root Session plus caller `Idempotency-Key` | Server derives the tree operation and stable per-target operation IDs |

The direct API mapping, authentication scope, and response contract are authoritative only in
[`agent-api.md`](agent-api.md). The private operation ID is never serialized externally; replay or
query resolves the original public key to that same identity. Cascade membership and its derived
per-target identities remain authoritative in
[`subagents.md#cascade-stop`](subagents.md#cascade-stop). A direct Turn stop does not redefine the
cascade contract.

For caller-owned keys, a missing key is rejected before a durable write or external effect. After an
authenticated, authorized, parseable keyed command reaches its `AdmissionScope`, the same caller
intent returns the original operation or tombstone and a changed caller intent conflicts. Internal
coordinators persist the scope and operation identity before sending a command. They cannot leave a
caller waiting on an unqueryable effect.

One AgentSession has at most one active Session operation. Historical operations remain queryable
by their original identity after completion, response loss, supersession, or restart. The canonical
operation projection and kind-specific rules are in
[`conventions.md#canonical-sessionoperationread`](conventions.md#canonical-sessionoperationread).

## Activity and Transcript

### Activity

AgentSession has only these Activity states:

| Value | Meaning |
|---|---|
| `idle` | No current-generation Turn or operation is nonterminal or uncertain. This is the only safe idle state. |
| `active` | A Turn is queued, running, or `outcome_pending`, or a known Session operation is progressing. |
| `unknown` | Input acceptance, a Turn result, a Runtime effect, Binding, or an operation cannot be confirmed. |

```text diagram
idle -- accepted Input ----------------------------> active
active -- all current work settles definitively ---> idle
active -- final result still expected -------------> active (outcome_pending)
active -- acceptance or effect becomes uncertain --> unknown
unknown -- authoritative reconciliation -----------> active | idle | unknown
unknown -- explicit force-reset --------------------> unknown old facts + new current context
```

Activity is derived from the current `ContextGeneration`. Unresolved facts from older generations
remain visible through `unresolvedPrevious`, `unresolvedPreviousCount`, and `nextAction`; they do
not overwrite `currentContextActivity`.

`admission=ready` requires all of the following in the current generation: Activity is `idle`, all
Turns are terminal, no external side effect is unresolved, and there is no ActiveOperation.
Otherwise admission is `blocked` with a stable reason and next action. An ordinary new Turn,
Compact, Reset, or automatic missing recovery must use this canonical admission result rather than
rederive safety from historical events.

A steer on a known running Turn is the only ordinary Input exception. It still requires explicit
Runtime support, the same complete Binding, and no competing operation. It never converts
`unknown` into safe idle.

### Transcript contract

SessionInput and AgentTurn are child records, not independently mutable aggregates. AgentSession is
the only authority for Input order, Turn ownership, and transitions. Transcript is one flat,
append-only sequence of Session facts; IDs provide stable association but do not form a message
tree or physical Session history.

`outcome_pending` means Input and dispatch are known but no final Turn result is recorded.
`unknown` means acceptance, side effect, or result cannot be confirmed. Neither state authorizes an
ordinary new Turn or context operation, and neither is replayed automatically.

A Binding replacement writes this user-visible boundary before later input:

```json
{
  "type": "session.context_reset",
  "payload": {
    "reason": "reset | runtime-change | missing-recovery | force-reset | handoff | rebind",
    "contextGeneration": 2,
    "operationId": "op_...",
    "observedAt": "2026-07-22T10:03:00Z"
  }
}
```

The boundary means only that later Runtime context starts empty. It contains no physical Session
history. Reset, Runtime change, missing recovery, force-reset, handoff, and rebind increment
`ContextGeneration`, commit their Binding/context result, and append this fact atomically. Compact
records a boundary result without changing the Binding or `ContextGeneration`.

`session.closed`, `session.followup_completed`, and `session.followup_failed` are not target event
types. Input and Turn express acceptance and execution separately. Consumers must not infer current
Activity from historical completion, failure, or stop facts.

## Follow-up and Cancel

Follow-up has two semantic paths:

| Current state | Accepted relation | Result |
|---|---|---|
| idle, admission ready | `new-turn` | create one Input and one new Turn |
| running, steer supported, no competing operation | `steer` | create one Input attached to the current Turn |
| running, steer unsupported | `new-turn` | queue a later Turn in Session order if capacity permits |
| `outcome_pending`, `unknown`, or context operation active | none | reject without guessing a target Turn |

The Session request map is the `AdmissionScope`
`(callerId, projectId, session-followup, sessionId, requestId)`. Acceptance, a
durable pure-rejection tombstone, or an uncertain result is persisted under that
identity. Same-key replay reads the stored Input/Turn/operation or tombstone and
cannot create another one. A queue-full or catalog-unavailable rejection occurs
before an Input is accepted but after the keyed admission scope, so it is also a
stable tombstone; retry after capacity or catalog recovery requires a new request
ID. Authentication, authorization, malformed envelope, and missing/invalid key
fail before that scope and create no tombstone.

An accepted new-turn Input and its dispatch fact commit before asynchronous enqueue. Queue or
process failure after that point cannot erase acceptance. Dispatch retry uses the original
attempt/work identity, remains bounded, and exposes `blocked` or `unknown` instead of minting a new
Turn. Dispatch schema and retry fencing are authoritative in
[`conventions.md#canonical-sessioninput-and-dispatch-schema`](conventions.md#canonical-sessioninput-and-dispatch-schema).

Steer persists the Input, target Turn, operation/effect identity, and replay obligation together.
Only a confirmed or safely replayable effect can be reported as accepted. Response loss first
queries the same effect identity; replay is allowed only when the adapter can apply that identity
idempotently and the complete fence still matches. A terminal or superseded target settles the
steer operation without moving the accepted Input. The authoritative adapter seam and result
mapping are in [`conventions.md#durable-steer-adapter-seam`](conventions.md#durable-steer-adapter-seam).

`cancel` is a Server-only control for one identified queued Turn and never contacts Runtime. A
cascade stop freezes a Session subtree under
[`subagents.md#cascade-stop`](subagents.md#cascade-stop). A direct API stop freezes one named Turn
through the external key mapping in [`agent-api.md`](agent-api.md). Neither path is a loose
running-Turn counterpart to cancel; each frozen target uses the same canonical rules below:

- a queued Turn is cancelled without contacting Runner;
- a running Turn is addressed only through its snapshotted Turn and complete expected Binding;
- Runtime stop is fenced before and after the external effect;
- an unconfirmed result leaves the target Turn and operation `unknown` and reuses the same derived
  target identity for query or bounded retry;
- a later Turn or changed Binding is outside the target and cannot be stopped by stale work.

Stopping a Turn does not terminate AgentSession. Stopping the initial Turn may settle its AgentJob;
stopping a later Turn never rewrites an already terminal AgentJob.

## AgentSession origins

Each AgentSession has exactly one immutable origin.

### Workflow origin

Address a Workflow-origin Session by `(projectId, workflowRunId, sessionName)`. Reusing the same
name in one WorkflowRun continues the logical Session. When no explicit name exists, use the Work
ID so unrelated tasks do not share context accidentally.

### Agent launch origin

Each Mohist Agent launch creates an Agent origin with the resolved Agent ID. One Agent can have many
AgentJobs and AgentSessions. Later Agent edits or archival do not change the Session origin or its
launch snapshot.

Matching Prompt, model, Runtime, Workspace, or configuration does not merge origins. An
origin-specific route is only a query convenience; it resolves to the canonical Session resource
and cannot define another lifecycle.

## Current Runtime Binding

AgentSession ID is the stable logical identity. `CurrentBinding` is the replaceable physical
routing fact:

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

Normal execution, retry, Follow-up, Compact, and Runner restart reuse the current Binding. Reset,
explicit Runtime change, confirmed-missing recovery, handoff, rebind, and force-reset may replace
the complete tuple without changing Session identity, origin, or work directory.

Every replacement compares the complete expected Binding and current operation fence, then commits
the candidate Binding, incremented `bindingEpoch`, Session revision, Context boundary, and post-CAS
fence as one atomic change. The returned post-CAS fence is the only token that can authorize later
writes. The canonical comparison and effect protocol are authoritative in
[`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence).

Every Runtime command and event carries the complete Binding tuple plus its Input, Turn, operation,
or dispatch identity when applicable. A late event from an old Runtime Session, Runner, binding
epoch, operation owner, or revision fails closed. It cannot change current Activity, Turn,
Transcript, or clean up a newer Binding.

The Runtime adapter owns physical Session cache, files, processes, and retention. Binding
replacement does not require AgentSession to retain, close, or continue querying the old physical
Session.

## Runtime Session missing recovery

Missing recovery repairs a current Binding; it is not Prompt replay, Workflow recovery, or Runner
migration. Transport failure, timeout, disconnect, or a missing local cache entry is not proof that
the Runtime Session is absent.

Automatic recovery is allowed only when the same Runner gives deterministic evidence that the
current Runtime Session is missing and the current generation is otherwise safe: Activity is
`idle`, admission is `ready`, no Turn is running or `outcome_pending`, and no Input, dispatch,
Runtime effect, or operation is `unknown`.

```text diagram
CurrentBinding
  | resolve on the same Runner
  +-- ready --------------------------> reuse CurrentBinding
  +-- definitely missing + safe -----> create candidate with stable key
  |                                      |
  |                                      +-- complete candidate --> fenced CAS
  |                                      +-- uncertain ----------> keep old Binding, block
  +-- absent evidence or uncertain ---> keep old Binding, block
```

When recovery is unsafe, Mohist retains the original Binding and Turn, sets
`admission=blocked`, and exposes `query_runtime_or_force_reset`. It must not infer missing, select a
different Runner, or replay Transcript.

### Recovery ownership and fencing

Recovery is a durable Session operation, not an in-memory window. One owner holds a bounded lease
and monotonically increasing `ownerFence` and `claimGeneration`. Restart or takeover can continue
the same operation only after the prior lease expires; an old owner then fails every write.

All external effects and persistence results use the complete `FenceToken` and `fenceMatch` from
[`conventions.md#canonical-effect-fence`](conventions.md#canonical-effect-fence). The Server checks
the token before the effect and again before persisting its result. Runtime validates the same
token at its side-effect boundary. No module may define a shortened recovery fence.

Candidate creation uses a stable key derived from the original operation. Response loss queries
that key before any retry. Only a complete candidate for the expected work directory,
runner/runtime, and next binding epoch may enter Binding CAS. An absent, rejected, incomplete, or
unknown candidate never authorizes adoption.

If ownership, revision, expected Binding, or candidate identity changes before adoption, the
candidate is an orphan. Cleanup uses an independent bounded operation and the exact candidate key
and Binding. It first proves that the candidate is not current or adopted, then discards it under
its own fence. Uncertain cleanup remains queryable as `cleanup-pending`; it does not keep a terminal
original operation active or block a later safe binding operation.

### Operation boundaries

| Operation | Automatic replacement after confirmed missing | Reason |
|---|---:|---|
| Initial TaskRun or AgentJob Input not yet submitted | Yes | It can continue in a known empty context without replaying an effect |
| Idle Follow-up | Yes | It starts a new execution through the same acceptance identity |
| Follow-up during execution | No | Replacement would change the physical target of that Input |
| Compact | No | A missing context cannot be compacted |
| Cancel or cascade-stop target | No | A replacement is not the original execution target |
| Ordinary Reset | No | Reset requires safe admission; unknown requires explicit force-reset |

Recovery never reconstructs Runtime context from Transcript. Transcript is an audit and
presentation record, not a command source.

## Context Operations

| Kind | Binding effect | Context effect | Key safety rule |
|---|---|---|---|
| `compact` | keep Binding | keep generation | unknown result stays on the same operation; do not compact a missing context |
| `reset` | replace on same Runner/runtime | increment generation | requires idle, ready admission |
| `recovery` | replace confirmed-missing Session on same Runner/runtime | increment generation | deterministic missing evidence only |
| `rebind` | replace on same Runner; runtime may change | increment generation | explicit request; never inferred from reconnect |
| `handoff` | replace on an explicit different Runner | increment generation | only this operation may change `runnerId` |
| `force-reset` | replace after explicit risk acknowledgement | increment generation | preserves and supersedes old unknown facts |
| per-target `stop` | keep Binding | keep generation | derived by cascade or direct API mapping; acts only on frozen Turn/Binding |

Each context operation has one stable canonical operation ID and request fingerprint before any
effect. Internal callers supply that ID, cascade derives its per-target IDs, and the direct API
durably maps its public key. Replay first returns the stored operation. A different intent cannot
join or overwrite another active operation.

Binding-changing operations follow one semantic order:

1. Persist operation identity, fingerprint, expected revision/generation/Binding, owner lease,
   target, stable candidate key, and deadline before any external effect.
2. Create or query only that candidate identity under the complete fence.
3. Record a validated complete candidate before attempting CAS.
4. Atomically change expected Binding to candidate and persist the post-CAS fence and Context
   boundary.
5. Use only the post-CAS fence for later input, result, completion, or cleanup decisions.

The detailed field set, null rules, comparison predicate, and CAS algorithm are defined once in
[`conventions.md`](conventions.md). This document does not restate their storage procedure.

Compact follows the same before/after effect fence but performs no Binding CAS. Success records a
ContextBoundary in the existing generation. A stale or lost result remains queryable on the same
operation and cannot be treated as a successful boundary.

### Force-reset

Force-reset is an explicit escape from current-generation `unknown`, not an automatic recovery or
ordinary Reset. It is allowed only when:

1. canonical current Activity or an ActiveOperation is actually `unknown` and blocks admission;
2. the caller supplies a new force-reset operation ID and explicitly acknowledges possible failure
   or duplicate external effects;
3. the request carries the revision and `ContextGeneration` from the same canonical read, and both
   still match;
4. Mohist can retain the old Input, Turn, operation result, and Binding while creating the new
   Context boundary.

Force-reset atomically records every current-generation unresolved Input, Turn, dispatch attempt,
Runtime effect, and ActiveOperation as a superseded target before adopting a replacement Binding.
It never rewrites an old unknown as success, failure, cancellation, or proof that the old physical
Session disappeared.

Candidate response loss keeps the same candidate key. A definitely absent or rejected candidate
may be retried with that key only within the original deadline; at the deadline the operation is
terminal `blocked`. An unclassifiable result remains `unknown`. Only an exact complete candidate
can enter CAS. A mismatched complete candidate enters independent cleanup; an unconfirmed binding
never does.

New Input can use the new generation only after the replacement Binding and Context boundary
commit. Response-loss query or retry returns the same operation, generation, binding epoch, and
superseded mapping. Old unresolved facts remain visible through `unresolvedPrevious` with their
original identities and a risk warning.

## Module Ownership

- Workflow owns TaskRun and the Workflow Action contract. It does not interpret Transcript.
- Agent owns Mohist Agent and AgentJob. It does not derive work results from Session Activity.
- Session owns AgentSession identity, source, work directory, Inputs, Turns, Activity, Binding,
  Transcript, context, and usage.
- Runner executes resolved work and reports physical facts. It does not arbitrate logical Session
  state.
- Runtime adapters hide provider SDK, protocol, process, cache, and error details. They do not
  define public Session identity or idempotency.
- Web, CLI, and trusted integrations consume canonical internal Server projections. Direct API
  callers consume only the public projection in [`agent-api.md`](agent-api.md). Neither derives
  current state from logs, provider responses, or historical terminal events.

Server is the sole arbiter for Binding, Activity, admission, and operation results. Runner cannot
independently replace a Binding or close an AgentSession because a process exited.

## Verification Boundaries

Tests must prove the contracts at deterministic seams without real Runtime, network, process,
filesystem Session, or wall clock:

- duplicate launch and Follow-up identities converge without duplicate Job, Session, Input, Turn,
  dispatch, or Runtime effect;
- work lifecycle and Session Activity never overwrite each other;
- `outcome_pending` and `unknown` never appear as idle or terminal success;
- stale owner, revision, Binding, Turn, and candidate facts fail closed before and after effects;
- response loss queries the same operation/candidate identity before any bounded retry;
- Binding replacement atomically advances epoch/generation and keeps Transcript and Session ID;
- cleanup cannot discard an adopted/current candidate;
- cascade stop acts only on frozen targets and never follows later Turns or Bindings;
- force-reset preserves old unknown facts and exposes the supersession mapping.
- malformed or unsupported execution configuration rejects before dispatch, while an unavailable
  exact persisted configuration remains waiting without a fallback;
- same-key replay after Agent-default or catalog change returns the original Job execution snapshot,
  including its catalog version and native mapping.

## Delivery sequence

| Target | Independent value | Dependency | Gap |
|---|---|---|---|
| Saved execution configuration | A saved Agent persists one statically validated Runtime, Model, ReasoningEffort, and Variant tuple with catalog defaults, while Variant remains independent from effort. | None. | [^433] |
| One-job execution tuning | A launch can override Model and ReasoningEffort for one Job, preview the resolved result, and read back its immutable versioned execution snapshot. | Saved execution configuration. | [^434] |

[^433]: Delivery gap [#433](https://github.com/suraciii/mohist/issues/433): saved execution configuration contract.
[^434]: Delivery gap [#434](https://github.com/suraciii/mohist/issues/434): one-job override and readback contract; depends on #433.

## Status

Stable AgentSession identity, Input and Turn records, Activity and unknown handling, launch and
Follow-up paths, and current Runtime Binding are implemented. The remaining gap is convergence:
not every ingress, aggregate boundary, and client consumes the same canonical operation and read
model yet.

- The saved Runtime, Model, ReasoningEffort, and Variant contract, its static catalog readiness,
  and its effective-tuple readback remain spec-first #433 work. The one-Job override, dry-run,
  and immutable readback that depend on it remain spec-first #434 work.
- Launch acceptance does not yet converge through one durable path from caller identity to every
  accepted or rejected Job/Session/Input/Turn result.
- Canonical internal projections are not yet the only state consumed by trusted clients. The
  direct API does not yet enforce its PAT-first admission and allowlisted public projection from
  [`agent-api.md`](agent-api.md) end to end.
- Confirmed-missing recovery is available for safely idle Workflow input. AgentJob initial Turns
  are already queued before that boundary, and idle Follow-up does not yet initiate it. Non-idle
  reconnect reconciliation can replace a binding without the complete proof that an old effect is
  absent. These paths do not yet share the owner lease, fence, candidate reconciliation, and cleanup
  contract.
- Web and CLI do not yet rely exclusively on canonical Server state for recovery, force-reset risk
  confirmation, and original-operation query.
- Some direct launch payloads still carry the legacy `workspacePath` context field. The named
  Workspace is the target identity; caller-supplied materialization paths are not part of the
  target contract.

Every Follow-up requires a non-empty caller `requestId`. Compact, Reset, recovery, handoff, rebind,
and force-reset require a caller `operationId`; steer reuses its Follow-up `requestId`. Some current
entry points still synthesize a hidden key when the caller omits one. This remains a safety gap
because response-loss retry cannot name the original intent.
