# Scheduled Input

## Scheduled Input

A schedule delivers one Input to a Session at an explicitly specified time. It
stores the text and due time immediately. At the due time, Mohist appends the
Input through the ordinary acceptance and execution path. A user or Agent may
create the schedule. This is not automatic inspection.

```bash
mo session schedule create <session-id> --at 2026-08-06T14:00:00+08:00 --text "Report current progress" --idempotency-key <key>
mo session schedule list <session-id>
mo session schedule cancel <session-id> <schedule-id>
```

- `--at` accepts only RFC 3339 with a time-zone offset, such as
  `2026-08-06T14:00:00+08:00` or `...Z`. A time without an offset or in the
  past is rejected.
- A schedule is one-time. There is no repeated schedule; create another one.
- An idle Session wakes and starts a Turn. A busy Session receives the Input in
  ordinary order.
- If Session state is unknown, the Input remains pending delivery and delivery
  continues after confirmation recovers. Mohist never pretends delivery or
  silently discards the Input.
- A schedule not yet delivered can be cancelled. Cancelling a delivered
  schedule does not change its Input. A schedule does not expire automatically.
- Stop, cascade stop, detach, Reset, and Compact do not delete schedules.
  Delivery that meets a stop waits until stop finishes. A detached Session
  still receives its scheduled Input.

The first version excludes repeated or periodic schedules, relative times,
attachments, automatic launch of a new Session at the due time, and schedule
display in `mo session view` or `mo session tree`.
