---
status: wip-not-implemented
---

# External Agent API

An external Agent or automation process sometimes needs to delegate work to a
configured Mohist Agent without opening the Web UI or pretending to be a Runner.
It also needs to survive response loss: after a disconnect, the caller must be
able to learn whether Mohist accepted the request without launching the same
work again.

The External Agent API will provide that private, headless boundary. It will use
the same AgentJob, AgentSession, Input, Turn, capacity, and recovery rules as Web,
CLI, and Connections. It will not create a separate execution lifecycle or give
the caller control of Runner and Runtime details.

## Boundary at a Glance

```text diagram
external caller
  -> Bearer PAT identity
  -> Scope and explicit private-Project grant
  -> stable request identity
  -> canonical Mohist Agent work
  -> allowlisted public state and result
```

This order exists for security as well as correctness. Mohist must reject an
unauthorized caller before it checks whether a resource or matching request
exists. Otherwise retries and error differences could reveal private Project
information.

## Who Will Be Allowed to Call

- Every direct caller will use its own personal access token. A Web session,
  Runner credential, or trusted Agent Connection identity will not substitute
  for that token.
- The token will name either an explicit set of private Projects or an explicit
  all-Projects grant. Operator capability alone will not imply Project access.
- Read-only tokens will observe known work. Operator tokens will launch,
  continue, or stop work within their Project grant.
- Mohist will check identity, capability, and Project grant before resource
  lookup, retry reconciliation, or creation of any work.

See [Authentication and Access](auth.md) for why these credential boundaries are
separate.

## One Request Will Mean One Intent

Every launch, follow-up, and stop command will include a caller-chosen stable
request key. The key lets the caller repeat a request after a timeout without
guessing whether Mohist received it.

- The same key and same accepted content will return the original canonical
  identities and latest public state.
- The same key with different content will be rejected. A caller must use a new
  key to express a new intent.
- Mohist, not the caller, will normalize the accepted request and calculate the
  fingerprint used for comparison.
- A definitive admission rejection will remain attached to the key. Later
  capacity recovery will not turn that rejected request into new work.

The launch key is isolated by caller, Project, and Agent. A follow-up key is
isolated by caller and Session, with Project and Agent derived from that
canonical Session. Reusing a convenient string in a different scope will not
join unrelated work.

## Launch and Follow-up Will Preserve Canonical Ownership

A launch will create one AgentJob and, after acceptance, its canonical Session,
Input, and Turn. If acceptance has not completed, callers can still observe the
AgentJob without inventing Session or Turn identities.

A follow-up will add one Input and one Turn to an existing Session. It will not
create a second AgentJob or let the caller switch the Session to another Project
or Agent.

This ownership matters because every product entry point must converge on the
same capacity decisions, results, and history. The direct API is an adapter over
Mohist work, not another scheduler.

## Public State Will Have Five Meanings

Every known Job, Input, and Turn will expose one aggregate state:

| State | Meaning |
|---|---|
| Accepted | Mohist durably knows the request but has not yet placed the current work in a queue. |
| Queued | Work is accepted and waiting for available execution capacity or another retryable condition. |
| Running | The Agent is processing the Turn or Mohist is still confirming its result. |
| Terminal | Work completed, failed, was cancelled, was permanently blocked, or was definitively rejected before execution. |
| Unknown | Mohist cannot yet confirm an execution, binding, stop, or result fact. |

Component facts will explain important distinctions without adding a second
lifecycle. In particular, a retryable capacity block remains Queued, and a
result that is still being confirmed remains Running. Unknown is not success,
failure, or permission to replay.

## Recovery Will Resume Observation, Not Work

Each Session will provide an ordered, durable stream of public updates. A caller
will save its last continuation position and resume strictly after it following
a disconnect.

- Repeated pages and concurrent readers will be safe to deduplicate by stable
  Session order.
- An invalid position will fail explicitly rather than jump to another Session
  or rebuilt stream.
- An expired position will require the caller to reload current public state and
  continue from retained history. Mohist will not silently reset the position.
- A durable context reset will appear as a public boundary with a safe reason,
  not as disclosure of the prior context.
- When projection facts have not caught up with canonical work, Mohist will say
  that observation is behind. Only a consumed but unresolved durable fact will
  become Unknown.

If a response is lost before a Session exists, the caller will observe the
AgentJob using the original launch key and returned Job identity. It will not
resubmit the prompt with a new key.

## Stop Will Be Idempotent and Fenced

A caller will request stop for one known Turn with a stable request key. Repeating
that key will return the same stop decision. It will not issue another stop or
target a replacement execution binding.

The first terminal execution or stop result will win. A late result cannot
rewrite the final state. If Mohist cannot confirm which result won, the Turn will
remain Unknown and new work that could conflict with it will stay blocked. The
caller must observe the Turn again rather than automatically replay the stop or
the original task.

## The Public View Will Be Deliberately Small

Direct callers need stable identities, public state, safe final output, safe
errors, timestamps, and ordered continuation. They do not need Mohist's internal
execution machinery.

The public view will never expose hidden Agent instructions, memory, prompt or
input text, raw tool or provider payloads, Runner or Connection identity,
Runtime Session identity, leases, fences, workspace paths, internal operation
keys, or diagnostic stack traces. Full product transcripts and controlled
diagnostics will remain separate surfaces with their own access rules.

This allowlist is the privacy boundary for a private-Project API. The feature
does not add multi-user transcript visibility, cross-user sharing, OAuth clients,
or a general Project ACL.

## Implementation Gaps

This page defines target product behavior only. The direct External Agent API is
not implemented. Project-bound PAT issuance, caller authorization before retry
lookup, durable launch and follow-up identity, public AgentJob observation,
five-state public projection, resumable Session updates, and fenced stop recovery
remain tracked by [#387](https://github.com/suraciii/mohist/issues/387).

Until that work is complete, do not treat this page as an available integration
surface or build automation against it.
