---
status: converged
---

# Agent Event Response

Event routing decides who responds; see
[`event-routing.md`](event-routing.md). This document defines the response
contract from launch through action and visible result.

## Model

A response is not a new entity. One response is one routing-triggered AgentJob
and its Agent-launch-origin AgentSession. Three authorities own its facts:

- AgentJob decides whether the response completed or failed.
- AgentSession records Agent actions.
- An Issue comment, when written by the Agent, explains the handoff to the
  owner.

## Semantics

### Guarantees

1. **At most once:** One Agent starts at most once for one event, independent
   of trigger path through routing rule, watch, or mention.
   [`event-routing.md`](event-routing.md) owns the durable launch-idempotency
   key.
2. **Use current state, not the event snapshot:** An event says what occurred.
   Before acting, the Agent must use the command surface to confirm current
   state. For example, it confirms that a run still waits for an Approval.
   Domain commands reject stale state explicitly. Approving a run that no
   longer waits is rejected. The Agent treats rejection as a normal signal, not
   as an internal error to retry.
3. **No serialization:** Responses for one Issue can run concurrently, such as
   routing plus an `@` mention or consecutive events. Do not add a per-Issue
   lock. Target aggregate command validation rejects conflicts without dirty
   state. Reevaluate only if real use produces harmful conflict.
4. **Response failure is visible:** Terminal AgentJob failure, including
   preflight failure, emits `com.mohist.agent.job.failed`. Stamping includes
   `agentid` and available business lineage such as Issue, Epic, and
   WorkflowRun. The event enters inbox and Hermes by default as an
   Agent-response-failed notification. The owner must not silently believe an
   Agent is handling work when it is not.
5. **Failure events are routable without self-response:** `agent.job.failed`
   uses the normal routing protocol. A rule whose AgentId equals envelope
   `agentid` does not match, based only on envelope data, and writes a structured
   log. This cannot stop a two-Agent cycle such as `A -> B -> A`. Such a cycle
   is user configuration and must be found through dry run and visibility.

### Attribution

Every Agent decision must be distinguishable from a person's action so an owner
can take over from history.

- A comment declares the Agent name through `--display-name`, by convention in
  the supervision preset.
- An Approval decision can declare `decidedBy`. Like comment author, it is a
  declaration rather than authentication. `mo run approve` and
  `mo run reject` accept `--display-name`. Decision events and read models
  include the field when present. An Agent should provide attribution; direct
  human action can omit it.
- Manual Approve and Send Back in the Web UI do not require an actor. An
  unsigned decision leaves `decidedBy` empty and does not synthesize `web`,
  `owner`, or another value.

### Non-goals

- A per-Issue response serialization lock.
- Automatic response retry. Job failure surfaces through
  `agent.job.failed`; retry is a new event or manual action.
- Trigger rate limits or cooldowns. Follow the Non-goals in
  [`event-routing.md`](event-routing.md).
- Suppression of direct notification for a supervised event. See the escalation
  model in [`agent-supervision.md`](agent-supervision.md).

## Status

The `agent.job.failed` event and notification, optional Approval `decidedBy`,
and Agent actor declaration are implemented. Guarantees 1 through 3 formalize
existing launch-pipeline and domain-command behavior.
