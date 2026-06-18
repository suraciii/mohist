## ADDED Requirements

### Requirement: Web UI visually distinguishes draft backlog issues

The Web UI SHALL visually distinguish draft backlog Issues from ready, pickable backlog Issues on both the board and the Issue Detail card. A draft Issue SHALL render a dimmed "Draft" indicator (or equivalent), and its Start affordance SHALL be disabled with the concrete reason. Draft indication SHALL be driven by the API-provided `isDraft` field, not inferred from labels, body text, or title.

#### Scenario: Board card shows draft state

- **WHEN** a backlog Issue has `isDraft = true`
- **THEN** the board card SHALL render a visible Draft indicator
- **AND** the card SHALL be visually de-emphasized relative to ready backlog Issues
- **AND** the card SHALL NOT represent the Issue as failed or blocked

#### Scenario: Issue Detail shows draft state

- **WHEN** a user opens Issue Detail for a draft Issue
- **THEN** the page SHALL show a Draft indicator
- **AND** the Start control SHALL be disabled
- **AND** the page SHALL explain that the Issue is still a draft

#### Scenario: Draft indicator is not inferred from labels

- **WHEN** the Web UI renders draft state
- **THEN** it SHALL use the `isDraft` field
- **AND** it SHALL NOT infer draft state from labels, the Issue body, or the title

## MODIFIED Requirements

### Requirement: Web UI shows start prerequisites on Issue Detail

Issue Detail SHALL display issue-level start prerequisites from API-provided `prerequisites` data, including whether each prerequisite issue has been delivered.

#### Scenario: Issue Detail lists prerequisite issues
- **WHEN** a user opens Issue #201
- **AND** Issue #201 has prerequisite issues #200 and #199
- **THEN** Issue Detail shows #200 and #199 as start prerequisites
- **AND** each prerequisite row indicates whether that prerequisite issue is delivered or waiting for delivery

#### Scenario: Issue Detail does not parse body text for prerequisites
- **WHEN** Issue Detail renders start prerequisite or readiness information
- **THEN** it SHALL use structured API fields such as `prerequisites`, `isDraft`, `canStart`, and `blocker`
- **AND** it SHALL NOT infer start prerequisites or draft state by parsing the Issue description

### Requirement: Web UI cards show the start blocker reason

Issue list/card surfaces SHALL show a concise start-blocker reason when server-provided start readiness reports that an Issue is not startable. The reason SHALL be derived from the `blocker` field (`Draft` or `WaitingFor(Issue)`), not from a `startEligibility` object.

#### Scenario: Card shows draft reason
- **WHEN** an issue card renders Issue #201
- **AND** Issue #201 has `blocker` of `Draft`
- **THEN** the card shows a concise Draft indicator or reason
- **AND** the card does not present the Issue as failed solely because it is a draft

#### Scenario: Card shows waiting reason
- **WHEN** an issue card renders Issue #201
- **AND** Issue #201 has `blocker` of `WaitingFor(Issue)` identifying Issue #200
- **THEN** the card shows a concise reason equivalent to `Waiting for #200`
- **AND** the card does not present the Issue as failed solely because it is waiting for prerequisite delivery

### Requirement: Web UI Start control respects server start readiness

The Web UI Start control SHALL use server-provided start readiness (`canStart` and `blocker`) to explain when an Issue cannot start, including draft and waiting-for-prerequisite states, and SHALL rely on the same Server API start guard when start is attempted.

#### Scenario: Start control disabled for a draft issue
- **WHEN** Issue Detail renders an Issue with `canStart = false` and `blocker` of `Draft`
- **THEN** the Start control is disabled or otherwise prevented from starting immediately
- **AND** the page explains that the Issue is still a draft

#### Scenario: Start control explains waiting for delivery
- **WHEN** Issue Detail renders Issue #201
- **AND** Issue #201 has `canStart = false` and `blocker` of `WaitingFor(Issue)` identifying Issue #200
- **THEN** the Start control is disabled or otherwise prevented from starting immediately
- **AND** the page explains that Issue #201 is waiting for #200

#### Scenario: Start attempt surfaces server rejection
- **WHEN** a user attempts to start Issue #201 from the Web UI
- **AND** the Server API rejects the request because Issue #201 is not startable
- **THEN** the Web UI shows the actionable server message
- **AND** it does not show that an agent session or pipeline run started
