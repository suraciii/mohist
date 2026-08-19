---
status: wip
---

# Scheduled Input Design

A scheduled input is a durable request to append an ordinary follow-up to an existing
`AgentSession` no earlier than an absolute due time. It is a Session capability, not a subagent or
session-tree capability. A parent may schedule input for a child because it knows the child's
Session ID, but the same operation works for any Session and requires no `SessionParentLink`.

See [`../docs/subagents.md#scheduled-input`](../docs/subagents.md#scheduled-input) for current product
behavior. The `AgentSession`, `SessionInput`, `AgentTurn`, and binding lifecycles remain authoritative
in [`agent-execution.md`](agent-execution.md).

## Why this boundary

The schedule record is the user's durable intent. An in-memory timer or reminder registration is
only replaceable wake-up infrastructure; even when that infrastructure has durable storage, it is
not proof that input was delivered. Activation loss, restart, or duplicate wake-ups must therefore
recover from the schedule record rather than infer state from a timer.

Delivery reuses ordinary follow-up admission because scheduling changes when an Input is admitted,
not what an Input means. This preserves one permission boundary, queue policy, binding policy,
transcript, and Runner protocol:

```text diagram
caller creates schedule
          |
          v
SessionSchedule (durable intent)
          |
          | due wake-up (replaceable)
          v
AcceptFollowup + stable delivery key
          |
          v
SessionInput / AgentTurn
```

The tree relationship and the schedule also have different lifecycles. Detach changes a
relationship; stop controls a frozen set of current work. Neither operation revokes explicit future
input for the target Session. Cancellation is the only action that withdraws an undelivered
schedule.

## Model

The target `AgentSession` owns each `SessionSchedule` alongside its Inputs and Turns. Keeping the
record with the Session serializes delivery, cancellation, and Input acceptance without creating a
second aggregate, inbox, or scheduler resource.

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
scheduled ---------> pending-delivery ---------> delivered
    |                       |
    +-----------------------+-------------------> cancelled
```

`delivered` and `cancelled` are terminal. `scheduled` means the due time has not arrived or a
wake-up has not yet started delivery. `pending-delivery` is the durable delivery-owed state: a
temporary blocker cannot move it backward or make it expire. Cancellation is its only exit other
than confirmed Input acceptance.

"Due but not yet started" is a transient observation, not another durable state. Recovery must find
a due `scheduled` record and start the same delivery path.

## Invocation contract

The creation body accepts only `text` and `dueAt`:

- `dueAt` must be an offset-bearing RFC 3339 timestamp (`Z` or `+/-hh:mm`) strictly later than the
  current Server time supplied by `TimeProvider`. Local time without an offset or a time that is not
  in the future is rejected; the caller should use an ordinary follow-up instead.
- `text` follows follow-up validation and must contain visible text. Scheduled attachments are not
  supported.
- Authorization is identical to follow-up: the caller must be allowed to operate the Project, and
  the Session must belong to it. The delivered Input gains no privilege beyond an ordinary
  follow-up.
- Creation does not require a current Runtime binding or activity. Those mutable facts are checked
  at delivery time, so temporary unavailability cannot erase future intent.

Creation is idempotent within `(ProjectId, SessionId, IdempotencyKey)`. The canonical fingerprint is
normalized text with leading and trailing whitespace removed plus UTC `dueAt`. Same-key replay with
the same fingerprint returns the original schedule and current state; a changed fingerprint is
rejected as a conflict. When the key is omitted, CLI prints a newly generated key before the request
and API generates a new key. Such separate requests are separate schedule intents; a caller that
needs response-loss replay must retain and reuse the key.

Cancellation is idempotent by target state and needs no separate key. It moves `scheduled` or
`pending-delivery` to `cancelled`. A `delivered` or already `cancelled` schedule returns its current
state, and an unknown Schedule ID is not found. Delivery and cancellation serialize in the target
Session; the first committed transition wins, and cancellation never deletes an accepted Input.

List returns all schedules for the Session, including terminal schedules, in ascending `DueAt`.
The current surface has no pagination.

The canonical CLI commands are:

```bash
mo session schedule create <session-id> --at <rfc3339> --text "<text>" [--idempotency-key <key>]
mo session schedule list <session-id>
mo session schedule cancel <session-id> <schedule-id>
```

## At-most-one logical delivery

Wake-ups are at least once, but each schedule may create at most one logical `SessionInput`. The
Server derives a stable follow-up identity from the schedule:

```text literal
IdempotencyKey: session-schedule:{scheduleId}
Source:         session-schedule
Text:           schedule.Text
```

Every initial attempt, recovery attempt, and response-loss replay uses that identity. Ordinary
follow-up admission therefore returns the original Input and Turn mapping instead of creating a
second one. On confirmed acceptance, the schedule records that Input ID and becomes `delivered`.
An unconfirmed result remains `pending-delivery` and reconciles the same identity; it cannot claim
delivery, mint a replacement key, or roll back to `scheduled`.

The schedule record and deterministic key associate the Input with its Schedule ID. Delivery adds
no generic provenance field, and the Runner receives only the ordinary follow-up envelope. Two
separately created schedules are two explicit intents and may each produce one Input.

## Wake-up and recovery

The Server is the sole trigger; Runner does not decide when a schedule is due. Delivery cannot
begin before `DueAt`. A one-shot reminder wakes each schedule at or after its due time, while a
recovery reminder scoped to the Session handles two failure classes:

- a due record still in `scheduled` because registration, activation, or the one-shot wake-up was
  lost;
- a `pending-delivery` record whose temporary blocker may have recovered.

Activation recreates missing wake-ups from nonterminal schedule records. The recovery reminder
exists only while that Session has a nonterminal schedule and scans only those records; unrelated
Sessions and terminal history do not add recovery work. Duplicate or late wake-ups are harmless
because they use the stable delivery identity.

Delivery then follows ordinary Session admission:

- `idle`: accept an ordinary follow-up and start a new Turn.
- `active`: accept it into the current or a later Turn in ordinary input order.
- `unknown`: keep `pending-delivery` and retry after activity evidence recovers.
- Runtime Session definitely absent while the Session is `idle`: establish a binding through
  automatic recovery in [`agent-execution.md`](agent-execution.md), then deliver.
- Runner unavailable, timeout, permission failure, binding change, or uncertain Runtime presence:
  do not create a binding; keep `pending-delivery` and retry after recovery.
- Follow-up capacity or Agent concurrency limit: keep `pending-delivery` and retry after recovery.
- Stop in progress: keep `pending-delivery` and retry after that stop operation completes.

Transport failure is not evidence that a Runtime Session is absent. Automatic binding recovery is
allowed only after determinate absence while the Session is idle; every ambiguous result fails
closed on the existing schedule and delivery identity.

## Lifecycle boundaries

- **Turn stop and cascade stop:** stop neither deletes nor cancels schedules. A delivery that meets a
  nonterminal stop operation remains `pending-delivery`. After stop completes, delivery may create a
  later Turn, which is outside the stop operation's frozen target snapshot.
- **Detach:** detach changes only `SessionParentLink`. A schedule remains owned by the same target
  Session and delivers when due whether that Session is attached or detached.
- **Reset and Compact:** neither deletes schedules. After Reset, delivery follows ordinary
  follow-up rules for the new binding; Compact does not involve schedule state.
- **Spawn:** a schedule targets an existing Session. It never launches or spawns a Session when due.

The session-tree side of these interactions is summarized in
[`subagents.md#scheduled-input-interaction`](subagents.md#scheduled-input-interaction).

## Non-goals

- No cron, recurrence, or relative time such as "in 30 minutes."
- No attachments or time-zone interpretation beyond an offset-bearing RFC 3339 absolute time.
- No automatic patrol or Server-created reminder intent. Only an explicitly created schedule can
  deliver input.
- No scheduled Agent launch or subagent spawn. Delivery appends to an existing Session and creates
  no `AgentJob`.
- No schedule projection in `mo session view` or `mo session tree`, and no Web management surface.
  API and CLI use the schedule list command.

## Status

Creation, listing, cancellation, durable schedule state, one-shot and recovery
reminders, stable follow-up identity, and the stop/detach/reset/compact
lifecycle boundaries are implemented in Server and CLI.

One target behavior remains open. When the physical Runtime Session is
definitively absent, due delivery currently stays `pending-delivery` until
another path restores the binding. Scheduled delivery does not yet initiate the
automatic confirmed-missing recovery described above. That recovery must reuse
the existing schedule and follow-up identities; it must not create a new intent
or treat transport failure as proof of absence.
