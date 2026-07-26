### Requirement: AgentJob terminal failure emits a failure event

When an AgentJob reaches a terminal failure state, the system SHALL emit a `com.mohist.agent.job.failed` CloudEvent so the owner can learn the agent is not handling the situation. Terminal failure includes every path that concludes the job as failed — runner-reported failure, preflight failure, job timeout, dispatch-retry bound exhaustion, and forced failure.

#### Scenario: Runner reports job failure
- **WHEN** the runner reports a failed result for an AgentJob and the job enters its terminal failure state
- **THEN** the system SHALL emit a `com.mohist.agent.job.failed` event exactly once for that job.

#### Scenario: Preflight failure prevents launch
- **WHEN** a routed-launch or watch-launch fails preflight (e.g. no capable runner, workspace unavailable) and the job concludes as failed before running
- **THEN** the system SHALL emit a `com.mohist.agent.job.failed` event for that job, the same as a runtime failure.

#### Scenario: Job times out
- **WHEN** an AgentJob exceeds its timeout and is concluded as failed
- **THEN** the system SHALL emit a `com.mohist.agent.job.failed` event for that job.

#### Scenario: Successful job emits no failure event
- **WHEN** an AgentJob reaches its terminal completed state
- **THEN** the system SHALL NOT emit a `com.mohist.agent.job.failed` event.

### Requirement: Failure event carries the agent identity and business lineage

The `com.mohist.agent.job.failed` event SHALL stamp the agent that failed and the business context it was serving, so recipients can route and attribute it. The `agentid` lineage key SHALL be required. Issue, epic, and workflow-run lineage SHALL be stamped when the job was serving such a context; their absence is valid for jobs without that context.

#### Scenario: Job serving an issue
- **WHEN** a failed AgentJob was serving an issue context
- **THEN** the emitted event SHALL carry `agentid` and the issue lineage, plus epic/workflow-run lineage when present.

#### Scenario: Job without an issue context
- **WHEN** a failed AgentJob has no issue context
- **THEN** the emitted event SHALL carry `agentid` and omit issue/epic/workflow-run lineage.

### Requirement: Failure produces an inbox item under a default-on notification kind

The system SHALL project `com.mohist.agent.job.failed` into the inbox under a dedicated notification kind "Agent 响应失败". This kind SHALL be enabled by default for every project so an unconfigured owner still sees the failure rather than silently believing an agent is handling it.

#### Scenario: Failure lands in inbox by default
- **WHEN** a `com.mohist.agent.job.failed` event is emitted and resolves an issue context
- **THEN** the inbox SHALL contain one item for that failure, attributed to the "Agent 响应失败" kind.

#### Scenario: Owner disables the kind
- **WHEN** the owner has disabled the "Agent 响应失败" notification kind for a project and a failure event resolves that project's issue context
- **THEN** the inbox SHALL NOT contain an item for that failure.

#### Scenario: Failure without issue context produces no inbox item
- **WHEN** a `com.mohist.agent.job.failed` event carries no issue context
- **THEN** the inbox SHALL produce no item for it (consistent with other events that cannot resolve an issue).

### Requirement: Failure pushes to Hermes under a default-on type

When Hermes is configured, the system SHALL deliver a push for `com.mohist.agent.job.failed` under the "Agent 响应失败" notification kind, enabled by default. The owner SHALL be able to turn it off.

#### Scenario: Configured Hermes pushes by default
- **WHEN** Hermes is configured, the "Agent 响应失败" type is enabled (default), and a failure event resolves an issue context
- **THEN** Hermes SHALL deliver a push notification for that failure.

#### Scenario: Disabled type suppresses the push
- **WHEN** the owner has disabled the "Agent 响应失败" Hermes type and a failure event occurs
- **THEN** the system SHALL NOT deliver a Hermes push for it.

### Requirement: The failure event is routable but cannot trigger the failing agent itself

`com.mohist.agent.job.failed` SHALL enter the routing protocol with the same standing as any other event. To prevent an agent from responding to its own failure, a routing rule whose configured `AgentId` equals the `agentid` on the event envelope SHALL be treated as a non-match. This check is envelope-only and SHALL be recorded in a structured log.

#### Scenario: Rule points at the failing agent
- **WHEN** a `com.mohist.agent.job.failed` event carries `agentid = A` and a routing rule's `AgentId = A`
- **THEN** that rule SHALL be treated as a non-match and SHALL NOT launch agent A, and the skip SHALL be logged as a structured event.

#### Scenario: Rule points at a different agent
- **WHEN** a `com.mohist.agent.job.failed` event carries `agentid = A` and a routing rule's `AgentId = B` (B ≠ A) whose match expression otherwise matches
- **THEN** that rule SHALL match normally and MAY launch agent B as a responder.

#### Scenario: Rule with no agent id
- **WHEN** a `com.mohist.agent.job.failed` event carries `agentid = A` and a routing rule has no configured `AgentId`
- **THEN** the self-response guard SHALL NOT suppress that rule; normal match evaluation applies.
