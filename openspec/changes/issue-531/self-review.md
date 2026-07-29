## Findings

### High: The projection consistency model violates mandatory poll recovery

`design.md` states that an AgentJob projection can be stale and therefore "skip or lose a poll opportunity" (lines 28-30). That conflicts with the required redelivery behavior in `specs/work-dispatch-ledger/spec.md`: a running owner work absent from the runner report SHALL be redelivered in that poll (lines 19-21), and the established scheduling invariant requires every running work to be corrected within one poll. A grain-state write followed by a best-effort projection mirror can leave a newly running job absent from the query used to calculate `desired`, so claim/report revalidation cannot repair the missing dispatch.

The plan must define a durable, poll-visible scheduling projection update protocol that cannot omit an owner transition from the next poll, including recovery after a write succeeds on only one side. T-001 and T-002 need acceptance criteria and tests for this write/interruption boundary.

### High: Capacity backpressure and pending availability timeout specify incompatible outcomes

`design.md` says that admission returns synchronous backpressure when every eligible runner is at capacity (line 36). The capability spec says an AgentJob that remains pending beyond its availability deadline SHALL fail as unavailable, and its "No runner can claim" scenario requires it to remain pending until that deadline (lines 71-76). A full-capacity runner is precisely a case where no runner can claim, but the plan does not say whether the launch is rejected without an AgentJob, creates a terminal failed job, or creates the pending job described by the spec.

The artifacts must choose one outcome for no eligible capacity, define the launch response and durable AgentJob state, and cover it in T-001 tests. The accepted outcome must be reflected consistently in the specification and design.

### Medium: The active-job migration has no deterministic `ReadySince` mapping

The migration plan requires backfilling `ReadySince` from existing AgentJob state JSON (design.md lines 79-80), but the current state has dispatch attempts, submitted time, runner identity, and running time rather than readiness time. The plan does not define which existing timestamp becomes `ReadySince`, how already-running jobs are distinguished, or how a pre-upgrade pending job avoids an immediate unavailable-runner timeout. This directly affects FIFO ordering and the new availability deadline.

T-001 must specify a deterministic mapping for every nonterminal legacy state and add migration specs using fixed time for pending, assigned-pending, running, terminal, and malformed/partial legacy rows.

<promise>FAIL</promise>
