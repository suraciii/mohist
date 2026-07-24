### Requirement: Fixed watch event set

Watch declarations SHALL launch only on the event types `stage.approval-requested` and
`run.failed`. The watch event set MUST NOT be configurable per declaration, per Agent, or per
issue; any other event type — including routing-rule-driven events — MUST NOT trigger a
watch-based launch. Custom event coverage is the domain of routing rules, not watches.

#### Scenario: Launch on approval-requested

- **WHEN** an event of type `stage.approval-requested` carries an issue on which an Agent is
  `watching`
- **THEN** that Agent is launched for that event

#### Scenario: Launch on run failed

- **WHEN** an event of type `run.failed` carries an issue on which an Agent is `watching`
- **THEN** that Agent is launched for that event

#### Scenario: No launch on other event types

- **WHEN** an event of any type other than `stage.approval-requested` or `run.failed` carries an
  issue on which an Agent is `watching`
- **THEN** no watch-based launch occurs for that Agent on that event

#### Scenario: Event without issue does not trigger watch

- **WHEN** an event of type `stage.approval-requested` or `run.failed` does not carry an issue
- **THEN** no watch-based launch occurs

### Requirement: Muted suppression of routing-rule hits

Before launching any Agent matched by a routing rule, the dispatch path SHALL check whether that
Agent is `muted` on the issue carried by the event. A `muted` declaration MUST suppress the launch
of that routing-rule hit, treating it as a non-match (the same disposition as an archived Agent),
and a structured log entry SHALL record the suppression. A `muted` declaration on one issue MUST
NOT affect routing-rule launches for that Agent on any other issue.

#### Scenario: Mute suppresses a rule hit on this issue

- **WHEN** a routing rule matches an Agent on an event, and that Agent is `muted` on the event's
  issue
- **THEN** the routing-rule launch is suppressed for that Agent on that event and a structured log
  is recorded

#### Scenario: Mute does not leak to other issues

- **WHEN** an Agent is `muted` on issue A, and a routing rule matches that Agent on an event for
  issue B
- **THEN** the Agent is launched on issue B as normal; only issue A is excepted

### Requirement: Per-agent launch idempotency

Launch idempotency for a single event SHALL be normalized to `(projectId, eventId, agentId)`.
When the same Agent is hit by both a routing rule and a watch declaration on the same event, the
Agent MUST be launched at most once for that event. Replay of the same event (same `eventId`)
MUST NOT produce a second launch or a second AgentJob for that Agent.

#### Scenario: Rule and watch on one event launch once

- **WHEN** a single event both matches a routing rule for an Agent and that Agent is `watching`
  on the event's issue
- **THEN** the Agent is launched exactly once for that event

#### Scenario: Event replay does not double-launch

- **WHEN** the same event (same `eventId`) is processed more than once for the same Agent
- **THEN** the Agent is launched at most once; no duplicate AgentJob is created

### Requirement: Built-in watch response prompt

Watch launches SHALL use a single built-in response prompt — event fact plus a directive to act
on the Agent's identity instructions — and MUST NOT carry any per-watch `ResponsePrompt`. The
prompt SHALL convey the triggering event as a fact (event type and the relevant issue context),
leaving response discipline to the Agent's own identity instructions.

#### Scenario: Watch launch uses the built-in prompt

- **WHEN** a watch-based launch is prepared
- **THEN** the prompt supplied to the launch is the built-in watch prompt (event fact + identity
  directive), not a caller- or declaration-supplied prompt

### Requirement: Launch-path reuse and provenance

Watch launches SHALL reuse the routed launch path: workspace resolution, preflight-failure
handling (a preflight failure records a failed AgentJob), and trigger tagging SHALL behave
identically to routing-rule launches. A watch-based launch SHALL annotate its trigger labels so
the launch source is marked as `watch`, preserving event↔session traceability.

#### Scenario: Watch launch reuses workspace and preflight handling

- **WHEN** a watch-based launch is dispatched
- **THEN** workspace resolution, preflight validation, and preflight-failure recording behave the
  same as a routing-rule launch

#### Scenario: Watch launch provenance is recorded

- **WHEN** a watch-based launch creates an AgentJob
- **THEN** the AgentJob's trigger labels mark the launch source as `watch`, distinguishing it from
  a routing-rule launch
