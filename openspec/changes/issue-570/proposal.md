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
