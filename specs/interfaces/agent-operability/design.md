# Agent Operability

The product contract lives in [`../specs/interfaces/agent-operability/spec.md`](spec.md);
user-visible syntax, fields, and error text live in
[`../specs/interfaces/cli/spec.md`](../cli/spec.md). This document records why the
control-plane write and failure contracts are shaped this way, and the rules an
implementation must preserve.

An Agent that operates Mohist retries after an unknown outcome. Until now the
control-plane writes had no durable identity: `issue start` and the Run controls
were addressed only by their target, so a caller that lost the response could not
distinguish "not executed" from "executed" and repeating the request created a
second effect. The read side already answers the next-step question; the write
side and the failure side did not.

## Design Drivers

- A duplicate control write must not create a second effect. Approving twice,
  requesting changes twice, or retrying twice produces a second feedback record,
  a second attempt, or a second transition.
- The caller, not the Server, decides when two requests are the same request.
  Only the caller knows that a lost response is the same intent as the retry.
- Control-plane writes are synchronous and state-guarded. The fence therefore
  has to separate three cases: not executed, executed, and still executing.
- Authorization and validation stay where they already are. A key makes a
  request repeatable; it never makes it permitted.
- An Agent must decide the next operation from structured output. A failure that
  only exists as prose forces the caller to guess whether a retry is safe.
- The existing factory stays authoritative. No second task model, no Agent-only
  route, no generic actor gate, and no new resource is introduced.

## Model

### Request fence

One durable row records one accepted write per caller-supplied key. The row
shape is shared with the external Agent API
([`agent-api.md`](../agent-api/design.md#normalized-fingerprint-and-idempotency)); the
control-plane commands use the same table so that "one durable identity per
write" has one implementation.

| Field | Meaning |
| --- | --- |
| `Command` | Operation family: `issue_start`, `workflow_control`, and the external Agent API's `launch`, `followup`, `stop`. |
| `ScopeKey` | Operation target + caller + key: `issue_start` uses `{projectId}\|{issueNumber}\|{callerKeyId}\|{key}`; `workflow_control` uses `{workflowRunId}\|{callerKeyId}\|{key}`. |
| `CallerKeyId` | Authenticated principal ID. One caller cannot replay another caller's key. |
| `Fingerprint` | SHA-256 over the canonical accepted payload: version, command, target, verb, and accepted body. |
| `State` | `pending`, `completed`, or `rejected`. |
| `Outcome` | JSON of the recorded response: HTTP status plus response envelope. |

The key is opaque, caller-chosen, and never interpreted as an ID. A mapping is
written before the operation runs and is never reclaimed by another request, so
a replay finds the original decision for as long as the row lives. Finished rows
are swept once they age past the retention window (seven days): inside the
window a repeated key replays the recorded decision, outside it the key is free
again and the caller owns the consequence. Pending rows are never swept — one is
either about to record its decision or, past the pending lease, taken over by the
next attempt.

`completed` records an accepted outcome. `rejected` records a classified
rejection: the Server decided against the operation, so a later retry of the same
key returns the same rejection instead of re-evaluating a decision the caller
already received. Unclassified failures (an unexpected exception, a 5xx) are not
recorded: the pending row is removed so the operation remains retryable.

Fingerprint input is the accepted payload only. Derived values (the resolved
Project, the current stage, the operator's display name, timestamps) never enter
the fingerprint, so two requests that differ only in Server-derived state stay
the same request.

### Failure projection

A control-plane failure answers three questions with fields, not prose:

- `effect`: `none` (the operation changed nothing), `unknown` (the caller cannot
  know whether it took effect), or `applied` (it took effect; the response
  describes the result).
- `retrySafe`: `true` only when repeating the identical request cannot produce a
  second effect.
- `nextAction`: one executable `mo` command, or a short instruction when no
  command exists.

`nextAction` is an executable command because the existing Runner status and
doctor reads already return commands, and because the CLI is the shared surface
for people and Agents.

### Control responses

An accepted control answers with the resource it changed: the Run read's own
composition — `status` (the `WorkflowStatusView`), `issueRef`, and
`workflowProfileId`. The caller asked for a transition, so the answer to that
one round trip is the resulting state and the `availableActions` that state
permits, in the same field names the Run read exposes.

The state read is a courtesy, not the operation's contract. A control whose
state read fails after the transition still reports its accepted outcome instead
of failing a request whose effect already applied; the Run route remains the
authoritative read.

Because the fence records the response body, a replay returns that recorded
resource — the state the first request produced, not the Run's state at replay
time.

## Semantics

### Keyed writes

Keyed replay covers the two write families an Agent needs to operate one Issue
and its WorkflowRun:

| Route | Command | Verb in fingerprint |
| --- | --- | --- |
| `POST /api/projects/{projectRef}/issues/{number}/start` | `issue_start` | — |
| `POST /api/workflow-runs/{workflowRunId}/{verb}` | `workflow_control` | `resume`, `approve`, `request-changes`, `retry`, `rerun`, `rerun-from-stage`, `pause`, `stop` |

`Idempotency-Key` is optional on both routes. A request without a key keeps its
previous behavior: it executes once and nothing is recorded. The CLI always sends
a key, and generates and prints one when the caller omits it, so a lost response
is always recoverable.

The key is a printable ASCII header of 1 to 128 characters. A malformed key is
rejected before any fence row, domain write, or external effect, with
`idempotency_key_invalid`.

Order for a keyed request:

1. Authenticate and authorize. Authentication and authorization failures are
   terminal before the fence: they neither read nor write a mapping.
2. Validate the key and normalize the accepted body.
3. Claim the fence row. An absent row is inserted as `pending` before the
   operation runs; an existing row is returned to the caller.
4. Classify the existing row:
   - different fingerprint → `409 idempotency_key_reused`, no domain write;
   - `completed` or `rejected` → replay the recorded status and body verbatim,
     no domain write;
   - `pending` and younger than the pending lease (30 seconds) → `503
     operation_pending` with `Retry-After: 1`, because another request is
     executing the same key;
   - `pending` and older than the lease → the earlier request was abandoned; the
     retrying request takes the fence over and executes.
5. Execute the operation. A classified outcome (accepted, or a rejection with a
   stable code) is recorded on the row and returned. An unclassified failure
   removes the pending row and propagates.

A `pending` row older than the lease is the only case where a key can be
executed twice. Re-execution is safe because control-plane operations are
state-guarded: a change that already happened is rejected by the guard rather
than applied twice, and the caller still receives one recorded outcome. The
lease is a bound on how long one caller's decision is protected from another,
not a promise about the control's own duration: a control that runs longer than
the lease can be taken over while it is still executing, and the state guard —
not the fence — is what keeps the effect single. The first recorded decision is
the one the key keeps.

A domain state guard refuses the operation before it changes anything, and the
refusal reaches the caller as a stable `conflict` code. It is therefore a
classified rejection: the fence records it, so a replay returns the same refusal
and a different payload under the same key is a conflict. A caller that wants the
operation re-evaluated after the state changed uses a new key — the recorded
rejection never becomes an approval.

`issue start` keeps its grain-level reuse: starting an Issue whose workflow is
already active returns the same WorkflowRun ID. The fence adds response replay
and conflict detection on top of that; it does not replace it.

### Read projection

An Agent decides from the existing resource commands; no status command is
added.

- `issue view` already exposes `status`, `canStart`, `blockedReason`,
  `attention`, `workflowRunId`, `workflowStage`, and `workflowStatus`.
- `run view` exposes the run identity and state plus the decision fields the read
  model already computes: `pendingWork` (what is executing), `failure` (why a
  stage stopped), `availableActions` (the next permitted Run controls), and
  `assignedTo`.
- `run why` stays the diagnosis read for a stopped or confusing Run.

The CLI catalog is the projection: it lists the fields a caller may select, and
it must not invent a field the Server DTO does not own.

### Failures at the CLI

The reference owns the format. On the Issue and Run commands — the surface an
Agent operates one Issue with — every failure carries the decision facts, in
both human and structured form:

- Human form: `error: <cause> [<code>]`, then `hint: <next action>` when a
  recovery exists.
- Structured form: when the invocation selects `--json`, the same failure is one
  JSON object on stderr: `code`, `message`, `effect`, `retrySafe`, and
  `nextAction` when one exists. The exit code is unchanged, and stdout stays
  empty on failure.

The Server supplies `effect`, `retrySafe`, and `nextAction` when it knows them
(the fence failures and the control-plane rejections). The CLI derives the rest:

- local usage failure → `effect: none`, `retrySafe: false`;
- request never submitted (transport failure before a response) → `effect:
  unknown`, `retrySafe: true` only for a keyed write, with the same command and
  the same key as the next action;
- response unreadable or malformed → `effect: unknown`, `retrySafe` by the same
  rule;
- read failure → `effect: none`, `retrySafe: true`;
- the caller's own cancelation or deadline → `canceled` (exit `130`) or
  `timeout`, with the same effect rule, so an interrupted keyed write is never
  reported as an operation that did not happen.

Automatic transport retry stays limited to keyed writes, which the fence makes
safe; an unkeyed write is never re-sent. A keyed write is re-sent once when the
connection failed, when the client's own timeout expired, or when the response
body was lost after the Server answered.

Every recovery command the CLI prints is complete — the run or issue reference,
the flags, and the key — and quoted for a POSIX shell, so it survives a copy
even when the caller's values contain spaces or quotes.

## Examples

Lost response to a keyed retry:

```text literal
mo run retry wr_abc --idempotency-key k1     # connection fails after the Server applied the retry
mo run retry wr_abc --idempotency-key k1     # replays the recorded outcome, no second attempt
```

Same key, different inputs:

```text literal
mo run request-changes wr_abc --message "tighten the check" --idempotency-key k1
mo run request-changes wr_abc --message "loosen the check"  --idempotency-key k1
# 409 idempotency_key_reused, effect none, no second feedback record
```

Structured failure:

```json
{"code":"idempotency_key_reused","message":"...","effect":"none","retrySafe":false,
 "nextAction":"mo run retry wr_abc --idempotency-key <new-key>"}
```

## Status

Implemented in this increment:

- `issue start` and the Run-scoped controls accept `Idempotency-Key`, record one
  fence row per accepted request, and replay accepted or rejected outcomes.
- The CLI sends a key for those commands, prints a generated key before the
  request, and reports structured failures.
- An accepted Run control answers with the Run resource it changed, so `--json`
  on a control selects the same fields `run view` reports.
- Finished fence rows are swept after the seven-day retention window.
- `run view` projects the decision fields the read model already computes.

Not implemented, and not required by this increment:

- the issue-scoped control axis (`POST /api/projects/{p}/issues/{n}/{verb}`) is
  unkeyed; the CLI uses the Run-scoped axis;
- keyed replay for other control-plane writes, cursor-based `run watch`, generic
  approval packets, and a capability catalog.
## Run Diagnostics

Explaining a failed WorkflowRun must be one request. The Server assembles
the facts; a CLI renders them. This spec defines the two read-only
diagnostics surfaces: `mo run why` and `mo doctor`.

### Design Drivers

- Failure triage today joins Run state JSON, task logs, dispatch payloads,
  and Runner registries by hand. Issue #655's triage needed all four; no
  surface answered the question. Diagnosis cost must not scale with fact
  dispersion.
- A diagnosis is a read model over existing facts. It adds no stored
  resource, no write path, and no lifecycle of its own.
- Operator-facing paths are logical. Process-scoped Runner internals, such as
  directory-handle paths, never appear in diagnosis output.
- The assembly contract is language-independent, so the Go CLI migration
  consumes it unchanged.

### Model

A **Diagnosis** is assembled for one WorkflowRun:

```text literal
Diagnosis
  Failure                  # run failure: reason, stage, task id, error
  Tasks[]                  # the failed task first, then its stage tasks
    TaskId, Attempt, Uses
    RenderedWith           # with-input after template rendering
    Workspace              # logical path, named | fallback, branch
    ExitCode, Error
    Recovery               # handler applied, budget remaining
  Dispatch                 # the persisted dispatch snapshot (payload freeze)
  Events                   # bounded recent run-event window
```

A **Doctor check** is one deployment fact:

```text literal
DoctorCheck
  Name                     # revision-alignment | migrations |
                           # verification-command | model-catalog
  Status                   # ok | fail
  Detail, NextAction
```

### Semantics

#### Assembly

- `GET /api/runs/{ref}/diagnosis` assembles the Diagnosis from run state,
  task attempts, the dispatch snapshot, and run events. It is read-only and
  serves live and terminal runs alike.
- Every task dispatch persists a dispatch snapshot on first issue
  (first-writer-wins). A snapshot lives as long as its Run and is deleted
  with it. A diagnosis reports `dispatch: missing` when no snapshot exists;
  that outcome is a defect signal, not normal state.
- Workspace identity in a diagnosis is the logical Workspace path and
  binding kind (`named` or `fallback`). Directory-handle paths and other
  process-scoped internals are mapped to the Workspace identity before
  rendering.

#### Rendering

- `mo run why <run>` renders the Diagnosis. The default view is the failure
  chain; `--json` selects fields. Exit status is 0 whenever a diagnosis is
  rendered; an unresolvable Run reference is the only error.
- `mo doctor` evaluates the check list against the connected Server and
  local services. `revision-alignment` compares CLI, Server, Runner, and
  Slack against one revision. `verification-command` reports Projects whose
  built-in Profile runs would fail closed for a missing command.
  `model-catalog` reports runtimes whose discovered catalog is empty or
  incomplete. A failing check prints its next action; exit status is 1 when
  any check fails.

### Status

- The dispatch snapshot store records no rows on the live deployment.
  Diagnosing and closing that persistence gap is part of the first
  implementation slice.
- Diagnosis assembly, the doctor check list, and both CLI commands are
  unimplemented.
