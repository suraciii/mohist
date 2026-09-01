# Agent Event Response

An event response starts one AgentJob from an event-routing decision and
records the resulting Agent work. [`event-routing.md`](event-routing.md) owns
launch idempotency. This document defines response execution and attribution.

## Design Drivers

- An event records past facts. The responding Agent must verify current state
  before it acts.
- Routing, watches, and mentions can name the same work. One idempotency key
  must prevent duplicate starts without serializing unrelated responses.
- Owners must distinguish Agent decisions from human actions in history.

## Model

A response is not a new entity. It consists of one routing-triggered AgentJob
and the Agent-launch-origin AgentSession. When the Agent writes an Issue
comment, it explains the handoff to the owner.

```text diagram
        +-------+
        | event |
        +---+---+
            |
            v
        +-------+
        | route |
        +---+---+
            |
            v
 +---------------------+
 | check current state |
 +----------+----------+
            |
            v
   +----------------+
   | start AgentJob |
   +--------+-------+
            |
            v
    +--------------+
    | AgentSession |
    +-------+------+
            |
            v
  +------------------+
  | action or result |
  +------------------+
```

AgentJob decides whether the response completed or failed. AgentSession records Agent actions.

## Semantics

### Launch and Current State

- One Agent starts at most once for one event, independent of whether the
  trigger came from a routing rule, watch, or mention. The durable key is
  defined by [`event-routing.md`](event-routing.md).
- An event says what occurred. Before acting, the Agent uses the command
  surface to confirm current state. For example, it confirms that a run still
  waits at an Approval Point.
- Domain commands reject stale state explicitly. Approving a run that no longer
  waits is rejected. The Agent treats this rejection as a normal signal and
  does not retry it as an internal error.
- Responses for one Issue may run concurrently. Mohist adds no per-Issue lock;
  target aggregate validation rejects conflicting commands without dirty state.

### Failure

- Terminal AgentJob failure, including preflight failure, emits
  `com.mohist.agent.job.failed` with `agentid` and available business lineage
  such as Issue, Epic, and WorkflowRun.
- The failure event enters the inbox and Hermes by default as an
  Agent-response-failed notification.
- `agent.job.failed` uses the normal routing protocol. A rule does not match
  when its AgentId equals the envelope's `agentid`. Mohist bases this decision
  only on envelope data and records a structured log.
- This self-response rule cannot stop a two-Agent cycle such as `A -> B -> A`.
  Users must detect such configuration through dry run and visibility.

### Attribution

Every Agent decision must be distinguishable from a person's action.

- A comment records the authenticated Principal as its author.
  `--display-name` is presentation-only and cannot set or replace attribution.
- A decision at an Approval Point records the authenticated Principal in
  `decidedBy`. `--display-name` is presentation-only and cannot set or replace
  `decidedBy`. `mo run approve` and `mo run request-changes` accept it only for
  presentation. Decision events and read models include the authenticated
  identity and display name when present.
- Web UI Approve and Request Changes do not require an authenticated actor. An
  unsigned decision leaves `decidedBy` empty and does not synthesize `web`,
  `owner`, or another value.

## Non-Goals

- A per-Issue response serialization lock.
- Automatic response retry. Job failure surfaces through
  `agent.job.failed`; retry is a new event or manual action.
- Trigger rate limits or cooldowns. See [`event-routing.md`](event-routing.md).
- Suppression of direct notification for a supervised event. See
  [`agent-supervision.md`](agent-supervision.md).

## Status

The `agent.job.failed` event and notification, authenticated Principal
attribution for comments and Approval Point decisions, and presentation-only
display names are implemented. The launch and current-state rules use the
existing routing and domain-command contracts.
