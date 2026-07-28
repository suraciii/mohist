### Requirement: A DM task dispatches one launch through the Agent API

When the Owner sends a task DM to the Bot, the Connection boundary SHALL invoke the Agent API's idempotent launch with a stable call identity derived from the Slack message identity, producing exactly one AgentJob, exactly one AgentSession, exactly one first SessionInput, and exactly one first AgentTurn. The Connection SHALL NOT create a second launch path, a second kind of job, or a provider-specific execution surface; all execution authority remains with the Agent API.

#### Scenario: A task DM creates exactly one launch
- **WHEN** the Owner sends a DM containing a task to the Bot
- **THEN** exactly one AgentJob, one AgentSession, one first SessionInput, and one first AgentTurn are created via the Agent API

#### Scenario: A bare mention without a task does not create work
- **WHEN** the Owner sends a DM whose text is empty or contains only the Bot mention and no usable attachment
- **THEN** no AgentJob or SessionInput is created and the Bot asks the Owner to supply a task

### Requirement: The Bot reports acceptance, queue, or explicit rejection

Immediately after a DM task is processed, the Bot SHALL reply in the same DM conversation with one of: accepted (the input is durably accepted and execution is starting), queued (the input is accepted but execution is waiting for a slot), or an explicit rejection with an actionable reason. The Bot MUST NOT stay silent or report success before Mohist has durably recorded the outcome.

#### Scenario: Accepted task is acknowledged
- **WHEN** the Owner's task is durably accepted and an execution slot is available
- **THEN** the Bot replies in the same DM that the task was accepted and is executing

#### Scenario: Queued task reports its position
- **WHEN** the Owner's task is accepted but no execution slot is available
- **THEN** the Bot replies that the task is queued rather than reporting it as executing or failed

#### Scenario: Rejected task reports an actionable reason
- **WHEN** the bound Agent needs setup or the input is otherwise invalid
- **THEN** the Bot replies with an explicit rejection and an actionable reason, and creates no Agent resources

### Requirement: The final result returns to the same DM conversation

When the launch's AgentTurn reaches a terminal result, the Bot SHALL deliver a user-consumable summary of that result into the same DM conversation that originated the task. The summary SHALL include enough conclusion, evidence, and next step to act without leaving Slack, and MUST NOT forward hidden reasoning, raw tool output, or credentials. If the Job result cannot be confirmed, the Bot SHALL surface that uncertainty instead of fabricating a result.

#### Scenario: A completed task posts its result
- **WHEN** the AgentTurn for the Owner's task completes successfully
- **THEN** the Bot posts a result summary into the originating DM conversation containing the conclusion and next step

#### Scenario: A failed task posts its failure
- **WHEN** the AgentTurn for the Owner's task fails
- **THEN** the Bot posts a failure summary into the originating DM conversation without reclassifying the failure as a Slack delivery problem

#### Scenario: Unconfirmed result is surfaced honestly
- **WHEN** the AgentJob or AgentTurn result cannot be confirmed
- **THEN** the Bot reports the uncertainty in the DM conversation and does not fabricate a success or failure

### Requirement: A redelivered Slack message resolves to the same input

Because Slack delivers events at least once, the same Slack message identity SHALL always resolve to the same SessionInput and the same first AgentTurn, and MUST NOT create a second AgentJob, a second SessionInput, or a second AgentTurn. This SHALL hold across `mohist-slack` restart, Server restart, and any Slack-initiated redelivery within the platform's retry window.

#### Scenario: Slack redelivers the same event
- **WHEN** Slack delivers the same DM event more than once
- **THEN** every delivery resolves to the same SessionInput and no second job, input, or turn is created

#### Scenario: Restart does not duplicate accepted work
- **WHEN** `mohist-slack` or the Server restarts after a DM task has been accepted and the event is redelivered
- **THEN** the redelivery resolves to the original SessionInput and no duplicate work is created

### Requirement: This issue dispatches one task per DM, not a continuous conversation

This issue SHALL deliver exactly one launch per dispatched DM task. DM continuous conversation, a current AgentSession per DM conversation, New task switching, and in-Slack cancel or stop are out of scope and SHALL NOT be delivered here. Each dispatched task is therefore independent of any earlier DM task.

#### Scenario: A second DM task starts independent work
- **WHEN** the Owner sends a second task DM after the first task has completed
- **THEN** the second DM dispatches a new independent launch rather than continuing the first AgentSession

#### Scenario: No continuation semantics are implied
- **WHEN** the Owner sends a follow-up DM while an earlier task is still running
- **THEN** the behavior for that case is governed by a later issue, and this issue makes no commitment to continue, queue, or merge it into the running turn
