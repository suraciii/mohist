# Issue 570: Runtime Readiness Witness Before Claim

## Problem

The Server can claim pending workflow work before it knows whether the
Runner's selected runtime is ready. The Runner currently owns readiness in
process memory, while the poll contract has no readiness witness. This can
produce a claimed work item that is deferred indefinitely or has no renderable
dispatch after a runtime restart or OOM.

## Proposal

Add a connection- and runtime-generation-fenced readiness witness to the poll
request. The Server resolves a pending work item's runtime without mutation and
claims only when the current witness says that runtime is ready. Unknown
runtime identity or stale/missing witness is fail-closed for new claims.

The Runner continues polling while it has already-held work so terminal
results and acknowledgements converge. Runtime recovery never authorizes a
new input, replay, or inferred terminal result.

## Safety boundary

This proposal prevents claim-before-readiness for work whose runtime can be
resolved by the Server. It does not guarantee that an external runtime cannot
fail after a valid witness. That race remains governed by the existing
in-flight/result-uncertain protocol.

This proposal does not recover an in-flight execution after process death,
does not replay a Pi or OpenCode prompt, and does not treat a session file,
catalog, presence heartbeat, reconnect, or Runner restart as a terminal fact.

## Implementation blocker

`RunnerPollRequest` currently has only `InFlight` and `AwaitingAck`;
`WorkflowRunScheduleCandidate` exposes only `WorkflowRunId`; and
`ClaimAndRenderWorkflowAsync` calls `TryClaimWorkflowAsync` before translating
the work. A safe implementation must change the wire contract, add a
read-only workflow runtime projection, and update Runner polling together.
Until those pieces are available, a design-only change is safer than a
partial AgentJob-only gate.
