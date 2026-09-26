# Session Execution Evidence

Recorded Activity describes accepted work, not proof that a process is still
executing. A Session can retain an active Turn across a restart without a
current execution observation. Lists, summaries, CLI, and API consumers must
distinguish that record from confirmed current execution.

- Keep canonical Activity and Turn phase unchanged when evidence ages.
  **Needs verification** is an observation label, not a new Session lifecycle
  or terminal result. Queued work remains labelled queued, not running.
- Evidence supporting **Running** must identify the same Session and current
  Turn. A current owner observation must also match its Runner generation;
  a fresh Runner heartbeat alone does not confirm a Session. Owner
  confirmation follows the [Runner work-confirmation contract](../../runner/work-confirmation/spec.md).
  Loss of that source does not invalidate separate fresh, correctly bound
  activity evidence.
- An execution observation is fresh for five minutes after its source
  observation time, inclusive. A later read or page refresh must not renew
  that time. Missing or future source times cannot establish freshness.
  This is a display boundary, not an execution deadline or retention policy.
- When an executing Turn has no fresh matching activity or owner confirmation,
  show **Needs verification**, its recorded phase, last evidence time when
  known, and the reason (missing, aged, or superseded-generation evidence).
  Fresh matching owner confirmation can keep a quiet, long-running Turn in
  **Running** even when its last output is old. Show last output time separately.
- New matching evidence can restore the current-execution label; an event for
  another Turn or obsolete Runner generation cannot do so. Confirmed terminal
  results take precedence over earlier running observations.
- Idle Sessions remain in history and can accept input according to their
  existing contract. Unverified executing Sessions remain discoverable in a
  **Needs verification** group instead of being hidden or counted as confirmed
  running. Historical event feeds still retain their original entries.

Loss or age of evidence must not complete or cancel a Turn, release a capacity
owner, reclaim a Workspace, replay input, or enable otherwise unsafe actions.
Session activity and Runner occupancy remain independent facts; neither is
computed by counting the other's visible rows.

## Acceptance scenarios

- Evidence exactly five minutes old is fresh; older evidence cannot establish
  Running. Reading the same evidence later does not move its source time.
- Missing or future source times do not establish Running. A fresh matching
  owner confirmation can support a quiet Turn with old output.
- Losing owner confirmation does not prevent separate fresh matching activity
  from establishing Running. Evidence for another Turn or generation cannot
  do so, and a confirmed terminal result takes precedence.
- An unverified executing Turn remains discoverable without releasing its
  capacity or Workspace and without replaying its Input.

## Related contracts

[AgentOps activity](../../agent-ops/activity/spec.md) owns grouping, counts, and
card navigation. [Session operations](../recovery/spec.md#why-unknown-fails-closed)
own Stop, reconciliation, and safe actions; this observation contract does not
replace their lifecycle rules.
