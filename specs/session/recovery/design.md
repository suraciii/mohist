# Recovery Design

## Current Runtime Binding

AgentSession ID is the stable logical identity. `CurrentBinding` is the
replaceable physical routing fact:

```json
{
  "runnerId": "runner-...",
  "runtime": "opencode",
  "runtimeSessionId": "ses_...",
  "bindingEpoch": 7
}
```

Normal execution, retry, Follow-up, Compact, and Runner restart reuse the current
Binding. Reset, Runtime change, confirmed-missing recovery, handoff, rebind, and
force-reset may replace the complete tuple without changing Session identity,
origin, or work directory.

Every replacement compares the complete expected Binding and operation fence,
then commits the candidate Binding, incremented `bindingEpoch`, Session revision,
Context boundary, and post-CAS fence atomically. The returned post-CAS fence is
the only token that authorizes later writes. The comparison and effect protocol
are authoritative in [`conventions.md#canonical-effect-fence`](../../../design/conventions.md#canonical-effect-fence).

Every Runtime command and event carries the complete Binding tuple plus its Input,
Turn, operation, or dispatch identity when applicable. A late event from an old
Runtime Session, Runner, Binding, operation owner, or revision fails closed. It
cannot change current Activity, Turn, Transcript, or clean up a newer Binding.

The Runtime adapter owns physical Session cache, files, processes, and retention.
Binding replacement does not require AgentSession to retain or query the old
physical Session.

## Runtime Session missing recovery

Missing recovery repairs a current Binding. It is not Prompt replay, Workflow
recovery, or Runner migration. Transport failure, timeout, disconnect, or a
missing local cache entry is not proof that the Runtime Session is absent.

Automatic recovery is allowed only when the same Runner gives deterministic
missing evidence and the current generation is safe: Activity is `idle`,
admission is `ready`, no Turn is running or `outcome_pending`, and no Input,
dispatch, Runtime effect, or operation is `unknown`.

```text diagram
                          +----------------+
                          | CurrentBinding |
                          +--------+-------+
                                   |
                                   vresolve on same Runner
                             +----------+
                             | evidence +-----------------------------------+
                             +-----+----+                                   |
            +----------------------+-----+                                  |
            vready                       vmissing + safe                    |
+----------------------+   +--------------------------+                     |
| reuse CurrentBinding |   | create candidate, stable |                     |
+----------------------+   |           key            |                     |
                           +-------------+------------+                     |
                           +-------------+---------+                        |
                           vcomplete               vuncertain               |
                    +------------+    +-------------------------+ uncertain |
                    | fenced CAS |    | keep old Binding, block |<----------+
                    +------------+    +-------------------------+
```

When recovery is unsafe, Mohist retains the original Binding and Turn, sets
`admission=blocked`, and exposes `query_runtime_or_force_reset`. It must not
infer missing, select another Runner, or replay Transcript.

### Recovery ownership and fencing

Recovery is a durable Session operation. One owner holds a bounded lease and
monotonic `ownerFence` and `claimGeneration`. Restart or takeover continues the
same operation only after the prior lease expires; an old owner fails every write.

Server checks the complete `FenceToken` before an external effect and again
before persisting its result. Runtime validates the same token at its side-effect
boundary. No module may define a shorter recovery fence.

Candidate creation uses a stable key derived from the original operation.
Response loss queries that key before retry. Only a complete candidate for the
expected work directory, Runner, Runtime, and next binding epoch may enter
Binding CAS. An incomplete or uncertain candidate never authorizes adoption.

If ownership, revision, expected Binding, or candidate identity changes, the
candidate is orphaned. Cleanup uses the exact candidate key and Binding. It
proves that the candidate is not current or adopted before discarding it under
its own fence. Uncertain cleanup remains queryable as `cleanup-pending` and does
not keep the original operation active.

### Operation boundaries

Automatic replacement after confirmed missing is allowed for an initial AgentJob
Input not yet submitted and for an idle Follow-up. It is rejected during an
executing Follow-up, for Compact, for a Stop target, and for ordinary Reset.
Reset requires safe admission. `unknown` requires explicit force-reset.

Recovery never reconstructs Runtime context from Transcript. Transcript is an
audit and presentation record, not a command source.

## Context Operations

- `compact` keeps Binding and generation. An unknown result stays on the same
  operation, and missing context cannot be compacted.
- `reset` replaces Binding on the same Runner and Runtime and increments the
  generation. It requires idle, ready admission.
- `recovery` replaces a confirmed-missing Session on the same Runner and Runtime
  and increments the generation. It requires deterministic missing evidence.
- `rebind` replaces Binding on the same Runner and may change Runtime. It is
  explicit and never inferred from reconnect.
- `handoff` replaces Binding on a different Runner and increments the generation.
  It is the only operation that may change `runnerId`.
- `force-reset` replaces Binding after explicit risk acknowledgement and
  increments the generation. It preserves and supersedes old unknown facts.
- Per-target `stop` keeps Binding and generation and acts only on its frozen
  Turn and Binding.

Each context operation has one stable operation ID and request fingerprint before
effect. Replay returns the stored operation. A different intent cannot join or
overwrite another active operation. Detailed fields, null rules, comparison,
and CAS algorithm are defined once in [`conventions.md`](../../../design/conventions.md).

Compact follows the same effect fence but performs no Binding CAS. Success records
a ContextBoundary in the existing generation. A stale or lost result remains
queryable and cannot be treated as a successful boundary.

### Force-reset

Force-reset is an explicit escape from current-generation `unknown`, not an
automatic recovery or ordinary Reset. It is allowed only when:

1. Current Activity or an ActiveOperation is `unknown` and blocks admission.
2. The caller supplies an operation ID and acknowledges possible duplicate or
   failed external effects.
3. The request revision and `ContextGeneration` still match the canonical read.
4. Mohist can retain old Input, Turn, operation, and Binding facts while creating
   the new Context boundary.

Force-reset records every unresolved current-generation Input, Turn, dispatch
attempt, Runtime effect, and ActiveOperation as a superseded target before
adopting a replacement Binding. It never rewrites an old unknown as success,
failure, cancellation, or proof that the old physical Session disappeared.

A candidate response loss keeps its key. A definitely absent or rejected
candidate may be retried with that key within the original deadline. At the
deadline the operation is `blocked`; an unclassifiable result remains `unknown`.
Only an exact complete candidate enters CAS. A mismatched candidate uses
independent cleanup, and an unconfirmed Binding never enters CAS.

New Input can use the new generation only after Binding and Context boundary
commit. Old unresolved facts remain visible with their original identities and a
risk warning.

## Status

Current implementation gaps are:

- Launch acceptance does not converge through one durable path from caller
  identity to every accepted or rejected Job, Session, Input, and Turn result.
- Trusted clients do not yet consume only canonical internal projections. Direct
  API admission and allowlisted public projection are not enforced end to end.
- Confirmed-missing recovery is not uniform for safely idle AgentJob Input and
  idle Follow-up. Non-idle reconnect reconciliation can replace a Binding without
  proving that an earlier effect is absent.
- Web and CLI do not yet rely exclusively on canonical Server state for recovery,
  force-reset risk confirmation, and original-operation query.
- Some direct launch payloads still carry legacy `workspacePath`; named Workspace
  is the target identity and caller-supplied materialization paths are outside
  the target contract.
- Stop recovery still has divergent request and recovery paths, including a
  synchronous Session-to-AgentJob stop-unknown cycle and no deadline on recovery
  redelivery. The [Follow-up and Stop rules](../input-and-turns/design.md#follow-up-and-stop)
  are the target.
- Every Follow-up requires a caller `requestId`. Compact and Reset require the
  caller-owned `Idempotency-Key` header, which is the stable identity used to
  recover a lost response. The Server rejects either request before accepting
  an effect when that header is missing. Recovery, handoff, rebind, and
  force-reset have no public entry point yet and still need the same rule.

## Runtime Switch Context

Mohist keeps a logical Agent Session alive even when its runtime-specific
physical session is unavailable. The runtime may therefore be replaced without
creating a new logical session or a synthetic user turn.

### Design Drivers

Physical runtime history is disposable; canonical Session and Slack history are
durable. Copying a full transcript is expensive, provider-specific, and can
reintroduce stale runtime state. A new runtime must still know which logical
Session and workspace it serves. Buzz uses a stable system prompt plus
channel/thread context and exposes history commands; it does not send a fake
handoff user message for runtime replacement.

### Model

`AgentSessionId` is the stable logical identity. `runtimeSessionId` identifies a
provider's physical session and may be replaced. A replacement increments the
binding generation and records the new runtime and physical identity.

The replacement context is system/infrastructure context, not a user Input or
Turn. It contains the canonical Session ID, workspace, and source conversation
identity. The agent may use the existing bounded Session transcript/view
command (`mo session transcript <session-id>`); Slack history injection or a
new Slack history command is out of scope until separately specified.

### Semantics

When a bound runtime is unavailable, the Runner may select the configured
fallback runtime (Pi), create an empty physical session, and
atomically replace the binding. It then sends the original user input exactly
once to the new runtime. It must not create a new logical Session, replay prior
inputs, or emit a synthetic handoff user message.

The new runtime receives the standard system context and can request the
canonical Session transcript on demand through the existing read-only command.
History is bounded, ordered, and redacted. The system does not inject a full
transcript by default.

If replacement or binding CAS fails, the original binding and canonical history
remain unchanged and the Turn is reported as retryable/unavailable. A failed
replacement must not leave a partially adopted physical session as current.

```text diagram
old binding unavailable
  -> create fallback physical session
  -> persist (logical Session unchanged, runtime binding replaced)
  -> send original input once
  -> agent reads history only if needed
```

### Non-Goals

This does not migrate provider conversation files, summarize history, create a
new queue, or automatically repair a provider whose completion signal is broken.

### Status

This specification replaces the earlier proposal for a one-time user-facing
handoff prompt. The runner fallback and focused recovery tests are implemented;
full Server gate and live Slack runtime-switch acceptance remain pending.
