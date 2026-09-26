# Recovery

Recover a continuing Session without changing its logical identity.
[Inputs and Turns](../input-and-turns/spec.md) own the conversation lifecycle;
[execution evidence](../execution-evidence/spec.md) owns display freshness.

## Why Unknown Fails Closed

A lost response can hide success or failure. Repeating a request can duplicate
work or repeat an external side effect. Mohist treats `unknown` as a state to
reconcile, not as permission to retry with a new identity.

- Retry a lost response with the same caller key. Mohist returns or continues
  the original result.
- Reusing a key with different content is rejected. Use a new key for a new
  intent.
- Requests that require a key are rejected before acceptance when it is missing.
- Querying an operation never repeats its side effect.

Every Compact and Reset carries a caller key. A request without one is rejected
before acceptance; Mohist never substitutes a hidden identity. The caller owns
that key until the outcome is known: the CLI states the key it will send before
the request and names the exact retry command when the result is unknown, and
the Web keeps one key per Project, Session, and operation across navigation and
remount. After a known outcome, the next Compact or Reset is a new intent with a
new key.

The Server is the authority. `idle` permits a new Turn, Compact, or Reset;
`active` means work is queued, executing, or awaiting confirmation; `unknown`
blocks new work until the original operation is queried or reconciled.

Current-execution observations follow
[Session execution evidence](../execution-evidence/spec.md);
that contract does not change the operation rules below.

Stop is the only operation for ending work. A queued Turn is cancelled locally.
A running Turn is cancelled only after Runtime confirmation. An uncertain Stop
leaves the Turn and Session `unknown`.

Force-reset is the explicit escape when an old `unknown` cannot reconcile. It
requires risk acknowledgement, preserves unresolved history, starts a new
context, and accepts new work only after that context is established.

See [External Agent API idempotency](../../interfaces/agent-api/design.md#normalized-fingerprint-and-idempotency)
and [Agent execution design](../input-and-turns/design.md#work-lifecycle-and-session)
for detailed identity, fencing, and projection contracts.

## Why Logical and Physical Sessions Are Separate

An AgentSession is Mohist's stable logical identity and audit record. A Runtime
Session is the physical conversation held by an execution backend. Separating
them lets Mohist replace lost or reset Runtime context without losing Session
identity, transcript, working directory, Inputs, Turns, or product links.

Mohist normally reuses the current Runtime Session. A physical Session changes
only at an explicit context boundary:

- **Reset** starts empty Runtime context while preserving AgentSession.
- **Runtime change or rebind** replaces the physical Binding on the same Runner
  while the Session is safely idle.
- **Handoff** moves the Session to another Runner and is the only operation that
  changes `runnerId`.
- **Confirmed-missing recovery** replaces a physical Session only after the same
  Runner proves it is absent and no accepted work or side effect is uncertain.
- **Force-reset** establishes a new context after unresolved work with explicit
  risk acknowledgement.

A timeout, disconnect, permission error, or unavailable Runner does not prove
that a Runtime Session is missing. Mohist keeps the association, blocks new
work, and asks the user to query or reconcile the original operation. It does
not automatically rebind or replay uncertain work.

After replacement, later work starts with empty Runtime context. The transcript
retains earlier content for audit but Mohist does not replay it into the new
Runtime Session. Old unresolved facts remain visible and do not become current
activity.

An open AgentSession page must follow the same logical Session through a
Runtime replacement without a manual reload. It must show newly accepted or
queued Input even before a Runtime Session is available. Delayed content from
the previous Runtime Session must not appear as current execution. The
[Session timeline](../transcript/spec.md#session-timeline) provides the readable record and
the Raw diagnostic view.

See [Agent execution design](design.md#runtime-session-missing-recovery)
and [Action Contracts](../../workflow/actions/spec.md#shared-semantics-for-agent-execution-actions).

## AgentSession Operations

- **Follow-up** continues the conversation and creates no AgentJob.
- **Compact** reduces Runtime context without changing AgentSession or its
  current Runtime Session.
- **Reset** starts empty Runtime context and records a context boundary.
- **Stop** ends queued or active work for one Turn or a Session tree. Unconfirmed
  targets remain `unknown`.
- **Force-reset** starts new context after an unresolved `unknown` with explicit
  risk acknowledgement.

These operations change Session execution, not work ownership.

## Implementation Gaps

- Confirmed-missing recovery is not uniform for safely idle AgentJob Input and
  idle Follow-up. Non-idle reconnect reconciliation can replace a Binding
  without proving that an earlier effect is absent.
- Force-reset, Runtime rebind, and Runner handoff have no public CLI, Web, or
  API operation. Public recovery remains limited to Compact and Reset.
- Compact and Reset require a caller key end to end, but the operations without a
  public entry point — force-reset, Runtime rebind, and Runner handoff — still
  need the same rule.

---

Implementation source: `packages/server/src/Mohist.Server/Agent/` and
`packages/server/src/Mohist.Server/Sessions/`.
