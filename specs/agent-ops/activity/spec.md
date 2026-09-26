# AgentOps Activity

Activity views combine Runner and Session observations so users can distinguish
current work, available capacity, and records that need verification. The views
do not own execution state or repair the records they display.

## Dashboard execution summary

The Dashboard must distinguish current execution from old records that need
review. Its Runner headline and capacity use the
[fleet summary](#fleet-summary); Session labels and counts use
[current execution evidence](../../session/execution-evidence/spec.md).
The sidebar and board warnings must preserve those same meanings.

- Show queued, confirmed running, and **Needs verification** work separately.
  Do not infer current execution from a nonempty historical Session list or
  describe missing telemetry as a confirmed report of active Runner work.
- Every work card must identify its real target. A Session without an Issue
  link opens `/<projectName>/sessions/<sessionId>` and shows its Agent or
  Session label. Never manufacture Issue number zero or an `/issues/0` link.
  A genuine Issue card keeps its Issue link; Session evidence on it can link
  separately to the Session.
- A record without enough identity to build a valid link shows a non-clickable
  **Target unavailable** label. It must not fabricate an identifier.
- If a linked Issue or Session cannot be found, its page must show an explicit
  not-found message and a return link to the relevant list. Permission and
  network failures must remain distinguishable from a missing object; none
  may leave a blank detail pane.

A Session last observed executing seventeen days ago, with no current owner
confirmation, must appear under **Needs verification**, not as confirmed
running. If it has no Issue attribution, its card must open the Session even
when the Runner currently reports zero occupied slots.

## Fleet summary

An offline Runner can need attention while another Runner can accept work.
Dashboard, sidebar, board warnings, and CLI summaries must not turn a partial
outage into a claim that all dispatch is blocked.

- Show **Capacity available** when at least one online Runner has ready
  admission and known unused slots. Show other blocked or offline Runners as
  a separate warning. This is not a promise that every Agent can execute.
- When none qualifies, show **Capacity full** only if all otherwise eligible
  Runners have known full capacity. If eligibility or capacity cannot be
  established, show **Availability unknown** with the missing evidence. When
  all Runners have known admission blockers, show **Admission blocked** with
  those reasons. No registered Runners means **No Runners configured**.
- Show occupied and total slots for online, admission-ready Runners with
  known capacity. Include a Runner blocked only by `capacity-full` in this
  pool so a full pool remains visible. Do not label occupied slots as free.
- Show excluded Runner counts and their configured capacity separately,
  distinguishing offline, other admission blockers, and unknown occupancy.
  Do not count unknown occupancy as zero or add excluded capacity to the
  currently eligible denominator. If no occupancy is known, display unknown,
  not a fabricated zero-capacity pool.
- Each summary must retain the snapshot's `observedAt` and a link or command
  to inspect the underlying Runner reasons. Refreshing a page must not make
  an old source observation fresh.
- Agent-specific availability remains the Server's decision for that Agent's
  Runtime, model, and concurrency requirements. Fleet capacity cannot override
  that decision or reserve a slot.

For example, one ready Runner with zero of eight slots occupied and six
offline records with nine configured slots must show **Capacity available**,
**0/8 occupied**, and **6 offline / 9 configured slots, occupancy unknown**.
The primary summary must not say **Admission blocked** or **unknown/17**.
Keeping those six records must not prevent the summary from being correct.
Status reads must not delete, re-enroll, or otherwise repair Runner records.

## Source contracts

Runner presence, admission reasons, owner rows, and snapshot timestamps come
from the [Runner status projection](../../../docs/runner.md#runner-status-projection).
Session execution labels come from
[Session execution evidence](../../session/execution-evidence/spec.md).
The [Web UI guide](../../../docs/web-ui.md) owns the surrounding pages and routes.
