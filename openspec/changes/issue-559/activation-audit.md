# Activation and Finalizer Audit

This audit is against `origin/master` at `5a2cc1d22`.
It is a design-only result: the current tree does not contain a safe
end-to-end activation and terminal-finalization slice for a Workflow Agent
Action.

## Verdict

Do not add an activation endpoint, route a Job terminal result through
`TaskReport`, or switch `mohist/agent` to the handoff grain yet. Those changes
would create a path in which the AgentJob can be terminal while the Workflow
task remains running, or in which a replay applies task effects more than
once. The existing inline `mohist/agent` translation remains authoritative.

The merged handoff fence is still useful and complete within its declared
boundary: it freezes the Agent definition and completion snapshot and records
`Prepared`, `Rejected`, or `Accepted` without materializing execution
participants. This audit keeps that boundary explicit instead of making
`Accepted` imply that execution has started.

## Current Reference Graph

| Concern | Current authority | Current boundary | Missing fact |
| --- | --- | --- | --- |
| Agent execution | `AgentJobGrain` | `AgentJob` owns Runner claim, result, and terminal state | Workflow invocation and task-attempt lineage |
| Physical execution | `AgentSessionGrain` | Session owns Input, Turn, transcript, and runtime binding | A frozen handoff-to-turn binding usable by finalization |
| Workflow outcome | `WorkflowGrain` / `WorkflowRun` | `ReceiveTaskReportAsync` applies outcome and advances the run | Finalizer receipt keyed by the handoff delivery |
| Handoff | `WorkflowAgentHandoffGrain` | Accepted receipt is non-executing and has no participants | Durable activation cursor and participant commands |

The ownership split is consistent with `design/architecture.md`: one
aggregate transaction cannot write another aggregate, and cross-aggregate
progress must be a durable source event followed by an idempotent target
command. A synchronous Job-to-Workflow callback cannot supply that guarantee.

## Blocking Evidence

### 1. There is no Workflow activation command

`WorkflowAgentHandoffGrain` persists an immutable invocation and frozen
completion snapshot, but its accepted result does not expose a participant
materialization command. The existing direct-launch APIs in
`IAgentJobGrain` prepare or submit a standalone Job; they do not consume a
Workflow handoff plan or preserve its reserved Job, Session, Input, and Turn
identities as one replayable activation protocol.

Adding only a route would leave the following crash window unresolved:

```text
accepted handoff
  -> Job created
  -> response lost
  -> retry cannot tell whether Session/Input/Turn were created
```

A retry could create replacement participants, or a caller could submit a Job
that the Workflow does not know how to settle. A persisted activation cursor
and idempotent participant commands are required before any production caller
may use `Accepted`.

### 2. The existing Job terminal event is not typed Workflow transport

`IAgentJobGrain.ReportResultAsync` accepts only `runnerId`, `workId`, and a
generic `WorkResult`. Its terminal delivery state currently contains a
`ConnectionLaunchOrigin` and connection-oriented summary fields. The emitted
event therefore has no stable Workflow invocation id, task-run attempt,
Session/Input/Turn lineage, frozen completion evaluation, or Workflow
finalizer acknowledgement.

The event is suitable for its existing connection notification use. Reusing it
for Workflow settlement would either discard identity needed to reject stale
delivery or overload a Slack/connection contract with Workflow semantics.

### 3. The Workflow report path is an incompatible finalizer

`WorkflowGrain.ReceiveTaskReportAsync` accepts the active `workId`, Runner, and
`TaskReport`, then calls `WorkflowWorkLifecycle.ApplyTaskReportAsync`. That
path binds artifacts and mutates task output, status, recovery follow-ups,
variables, and advancement in the WorkflowRun transaction. It has no stable
terminal-delivery receipt or per-effect receipt.

The AgentJob terminal path independently persists its terminal state and
delivers Session/event obligations. It cannot atomically commit the Job
terminal and the Workflow task outcome. A new endpoint that translates a Job
terminal into `TaskReport` would therefore permit:

- Job terminal commit followed by lost Workflow delivery;
- duplicate delivery applying artifact or variable effects twice;
- a stale terminal delivery settling a later task attempt that reused a named
  Session; or
- a Workflow task being advanced by a result that does not identify the
  original AgentJob and Turn.

### 4. Readback is not settlement

`AgentJobGrain.GetTerminalResultAsync`, Session transcript/activity, and
existing CloudEvents are observations or projections. None is a Workflow-owned
write authority. Polling or reading one of them and then calling
`ReceiveTaskReportAsync` would bypass the frozen handoff snapshot and would
not make acknowledgement loss replay-safe.

## Negative Contract

Until all three missing contracts are deployed and replay-tested together:

1. `WorkflowItemTranslator` SHALL keep the current inline
   `mohist/agent` path. It SHALL NOT call handoff activation from a Workflow
   dispatch.
2. An `Accepted` handoff SHALL create no AgentJob, AgentSession, Input, Turn,
   Runner claim, or Runner work.
3. No AgentJob terminal event SHALL be routed to
   `WorkflowGrain.ReceiveTaskReportAsync` as a synthetic `TaskReport`.
4. No component SHALL infer Workflow task outcome from AgentJob status,
   Session activity, transcript, or a public read projection.
5. No retry SHALL resolve mutable Agent configuration again for a command whose
   handoff fingerprint or completion snapshot was already persisted.
6. Existing direct Agent launches and inline `mohist/opencode` and
   `mohist/pi` actions SHALL retain their current ownership and settlement
   paths.

These are product safety constraints, not temporary compatibility behavior.
Removing them requires the complete replacement protocol below.

## Minimum Safe Follow-up

The next implementation should be delivered as one dark-to-live sequence with
separate aggregate authorities:

1. **Activation:** persist an activation cursor on the accepted handoff and
   issue idempotent commands to materialize the reserved Job, Session, Input,
   and Turn from frozen facts. A lost acknowledgement retries the same
   participant step; it never allocates a replacement. There is no production
   Workflow caller in this step.
2. **Typed terminal delivery:** add a Workflow-specific durable Job outbox
   record with one stable delivery id. It must carry invocation, WorkflowRun,
   TaskRun, work, Job, Session, Input, and Turn identities, terminal facts, and
   the frozen completion evaluation. It must remain pending until the target
   acknowledges the matching delivery. It is a separate contract from the
   connection terminal event.
3. **Workflow finalizer:** add the finalizer to the WorkflowRun write authority.
   It must validate the frozen invocation against the active task attempt,
   persist an effect receipt before each task outcome, expectation, artifact,
   variable, recovery, or advancement effect, and return the same acknowledgement
   for a duplicate delivery. Stale or conflicting identities must have no
   effect.
4. **Cutover:** switch new `mohist/agent` dispatch only after activation,
   terminal delivery, finalizer, restart, acknowledgement-loss, duplicate, and
   stale-delivery tests pass on the same deployed contract.

## Evidence Status

- Static audit only; no production code changed in this slice.
- No live WorkflowRun, Runner, or issue state was mutated.
- No focused test is claimed because this commit adds no executable behavior.
