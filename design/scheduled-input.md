# Scheduled Input Design

A scheduled input is a durable request to append an ordinary follow-up to an
existing `AgentSession` no earlier than an absolute due time. It is a Session
capability, not a subagent or session-tree capability. A parent may schedule
input for a child because it knows the child Session ID, but the operation works
for any Session and requires no `SessionParentLink`.

Product behavior is defined in
[`../docs/subagents.md#scheduled-input`](../docs/subagents.md#scheduled-input).
The `AgentSession`, `SessionInput`, `AgentTurn`, and binding lifecycles remain
authoritative in [`agent-execution.md`](agent-execution.md).

## Design Drivers

- The schedule record is the user's durable intent. A timer or reminder is only
  replaceable wake-up infrastructure and never proves delivery.
- Activation loss, restart, and duplicate wake-ups recover from the schedule
  record, not from timer state.
- Delivery reuses ordinary follow-up admission. Scheduling changes when an
  Input is admitted, not what an Input means.
- Stop and detach have different lifecycles from scheduled input. Neither
  operation revokes explicit future input. Cancellation is the only withdrawal
  action.

```text diagram
+---------------------+
| caller creates      |
| schedule            |
+----------+----------+
           |
           v
+--------------------------+
| SessionSchedule          |
| durable intent           |
+------------+-------------+
             | due wake-up (replaceable)
             v
+--------------------------+
| AcceptFollowup +         |
| stable delivery key      |
+------------+-------------+
             v
+--------------------------+
| SessionInput / AgentTurn |
+--------------------------+
```

## Model

The target AgentSession owns each `SessionSchedule` alongside its Inputs and
Turns. This keeps delivery, cancellation, and Input acceptance serialized
without a second aggregate, inbox, or scheduler resource.

```text literal
SessionSchedule
  ScheduleId
  DueAt           Absolute due time (UTC)
  Text
  Status: scheduled | pending-delivery | delivered | cancelled
  InputId?        SessionInput recorded after delivery acceptance
  IdempotencyKey  Caller idempotency key at creation
  CreatedAt
  CancelledAt?
```

```text diagram
             +-----------+
             | scheduled +-----+
             +-----+-----+     |
               +---+           |
               v               |
     +------------------+      |
     | pending-delivery |      |
     +---------+--------+      |
       +-------+-------+       |
       v               v       |
 +-----------+   +-----------+ |
 | delivered |   | cancelled |<+
 +-----------+   +-----------+
```

`scheduled` means the due time has not arrived or delivery has not started.
`pending-delivery` is the durable delivery obligation. A temporary blocker
cannot move it backward or make it expire. `delivered` and `cancelled` are
terminal. "Due but not yet started" is a transient observation, not another
state.

## Invocation contract

The creation body accepts only `text` and `dueAt`:

- `dueAt` must be an offset-bearing RFC 3339 timestamp, using `Z` or
  `+/-hh:mm`, strictly later than the current Server time from `TimeProvider`.
  Local time without an offset and non-future time are rejected. The caller
  should use an ordinary follow-up instead.
- `text` follows follow-up validation and must contain visible text. Scheduled
  attachments are unsupported.
- Authorization is identical to follow-up. The caller must be allowed to
  operate the Project, and the Session must belong to that Project. Delivered
  Input receives no privilege beyond an ordinary follow-up.
- Creation does not require current Runtime binding or Activity. Those mutable
  facts are checked at delivery time, so temporary unavailability cannot erase
  future intent.

Creation is idempotent within
`(ProjectId, SessionId, IdempotencyKey)`. The fingerprint is normalized text
with leading and trailing whitespace removed plus UTC `dueAt`. Same-key replay
with the same fingerprint returns the original schedule and current state. A
changed fingerprint is rejected as a conflict.

When the key is omitted, CLI prints a newly generated key before the request and
API generates a new key. Separate requests are separate schedule intents. A
caller that needs response-loss replay must retain and reuse the key.

Cancellation is idempotent by target state and needs no separate key. It moves
`scheduled` or `pending-delivery` to `cancelled`. Cancelling `delivered` or an
already cancelled schedule returns its current state. An unknown Schedule ID
returns not found. Delivery and cancellation serialize in the target Session;
the first committed transition wins. Cancellation never deletes an accepted
Input.

List returns all schedules for the Session, including terminal schedules, in
ascending `DueAt`. The current surface has no pagination.

The canonical CLI commands are:

```bash
mo session schedule create <session-id> --at <rfc3339> --text "<text>" [--idempotency-key <key>]
mo session schedule list <session-id>
mo session schedule cancel <session-id> <schedule-id>
```

## Delivery semantics

Wake-ups are at least once, but a schedule may create at most one logical
`SessionInput`. Server derives a stable follow-up identity:

```text literal
IdempotencyKey: session-schedule:{scheduleId}
Source:         session-schedule
Text:           schedule.Text
```

Every initial attempt, recovery attempt, and response-loss replay uses this
identity. Ordinary follow-up admission returns the original Input and Turn
mapping instead of creating another one. Confirmed acceptance records InputId
and moves the schedule to `delivered`. An unconfirmed result keeps it
`pending-delivery` and reconciles the same identity. It cannot claim delivery,
create a replacement key, or return to `scheduled`.

The schedule record and deterministic key associate the Input with ScheduleId.
Delivery adds no generic provenance field, and Runner receives only the ordinary
follow-up envelope. Two separately created schedules are two explicit intents
and may each produce one Input.

## Wake-up and recovery

Server is the sole trigger. Runner does not decide when a schedule is due.
Delivery cannot begin before `DueAt`. A one-shot reminder wakes each schedule at
or after its due time. A Session-scoped recovery reminder handles two cases:

- a due `scheduled` record whose registration, activation, or one-shot wake-up
  was lost;
- a `pending-delivery` record whose temporary blocker may have recovered.

Activation recreates missing wake-ups from nonterminal schedule records. The
recovery reminder exists only while that Session has a nonterminal schedule and
scans only those records. Unrelated Sessions and terminal history create no
recovery work. Duplicate or late wake-ups are harmless because they use the
stable delivery identity.

Delivery then follows ordinary Session admission:

- **Idle:** accept an ordinary follow-up and start a new Turn.
- **Active:** accept it into the current or a later Turn in ordinary input
  order.
- **Unknown:** keep `pending-delivery` and retry after Activity evidence
  recovers.
- **Runtime Session definitely absent while idle:** establish a binding through
  automatic recovery in [`agent-execution.md`](agent-execution.md), then
  deliver.
- **Runner unavailable, timeout, permission failure, binding change, or
  uncertain Runtime Session presence:** do not create a binding. Keep
  `pending-delivery` and retry after recovery.
- **Follow-up capacity or Agent concurrency limit:** keep
  `pending-delivery` and retry after recovery.
- **Stop in progress:** keep `pending-delivery` and retry after the stop
  operation completes.

Transport failure is not evidence that a Runtime Session is absent. Automatic
binding recovery is allowed only after determinate absence while the Session is
idle. Every ambiguous result keeps the existing schedule and delivery identity.

## Lifecycle boundaries

- **Turn stop and cascade stop:** stop neither deletes nor cancels schedules.
  Delivery meeting a nonterminal stop remains `pending-delivery`. After stop
  completes, delivery may create a later Turn outside the stop's frozen
  snapshot.
- **Detach:** detach changes only `SessionParentLink`. The schedule remains
  owned by the same target Session and delivers whether the Session is attached
  or detached.
- **Reset and Compact:** neither deletes schedules. After Reset, delivery uses
  ordinary follow-up rules for the new binding. Compact does not change schedule
  state.
- **Spawn:** a schedule targets an existing Session. It never launches or
  spawns a Session when due.

The session-tree side of these interactions is summarized in
[`subagents.md#scheduled-input-interaction`](subagents.md#scheduled-input-interaction).

## Non-goals

- No cron, recurrence, or relative time such as "in 30 minutes."
- No attachments or time-zone interpretation beyond offset-bearing RFC 3339
  absolute time.
- No automatic patrol or Server-created reminder intent. Only an explicitly
  created schedule can deliver input.
- No scheduled Agent launch or subagent spawn. Delivery appends to an existing
  Session and creates no `AgentJob`.
- No schedule projection in `mo session view` or `mo session tree`, and no Web
  management surface. API and CLI use the schedule list command.

## Status

Creation, listing, cancellation, durable schedule state, one-shot and recovery
reminders, stable follow-up identity, and stop, detach, Reset, and Compact
boundaries are implemented in Server and CLI.

When the physical Runtime Session is definitively absent, due delivery currently
stays `pending-delivery` until another path restores the binding. Scheduled
delivery does not yet initiate the automatic confirmed-missing recovery
described above. That recovery must reuse the existing schedule and follow-up
identities. It must not create a new intent or treat transport failure as proof
of absence.
