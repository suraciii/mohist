# Issue 570: Runner Result Delivery Recovery

## Problem

The Runner keeps dispatch and report state in process memory. If the process is
restarted after a work item has produced a result but before the server has
durably acknowledged that result, the server can preserve the original
Workflow execution as unresolved while the Runner loses the result delivery.

## Change

Add a Runner-local, atomic work-result journal. The journal records the exact
dispatch identity before execution, records the result before the first report,
and removes the record only after a durable server acknowledgement. A restarted
Runner redelivers only completed journal entries with the original identity.

## Safety Boundary

An entry recorded only as `started` is a recovery fence, not permission to
execute again. Runner restart, AgentSession activity, idle state, or a missing
runtime process cannot establish a Workflow task outcome. Existing unresolved
and blocked Workflow work therefore remains subject to the explicit stop and
authoritative-result paths; this change does not release or guess-replay it.

When a restarted Runner has a durable `started` entry for an identified
Workflow Agent task, it may report only the non-terminal
`agent-result-unconfirmed` observation with the original task attempt and work
identity. The Server's durable acknowledgement of that observation permits the
Runner to retire the fence. This observation starts the existing unknown/blocked
settlement path; it never supplies a task result, infers an outcome from an
idle/runtime/artifact fact, or authorizes a replacement execution.

The same startup receipt applies to an identified AgentJob dispatch. The Server
must validate the original Runner, AgentJob, and work identities and enter the
Job's durable `Unknown` state rather than converting the observation into a
failed terminal result. This closes the restart gap for both dispatch owners;
it does not replay the AgentJob or infer a terminal result.

## Server Receipt Boundary

The Server already has one safe admission path for a recovered result: the
normal Workflow result report with the original runner, task attempt, and work
identity. A completed journal entry contains that full result and can use the
path after the Workflow has become unknown or blocked.

A `started` entry is not a terminal result receipt. It contains no result
payload, so the Server must not convert it, an AgentSession idle/completed
observation, a turn status, or a terminal task log into Workflow task success or
failure. The recovery receipt only records the non-terminal Unknown fact for
the exact original owner identity. When no completed receipt can be replayed,
the Workflow attempt remains unresolved and an AgentJob remains Unknown. A
later physical execution is not supplied by this recovery slice. The current
only Workflow abandonment control is explicit Workflow stop; if a later
product capability schedules replacement after that abandonment, it must use a
new task/work identity.
