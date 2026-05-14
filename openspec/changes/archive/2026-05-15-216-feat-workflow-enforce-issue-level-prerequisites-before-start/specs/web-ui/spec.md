## ADDED Requirements

### Requirement: Web UI shows start prerequisites on Issue Detail

Issue Detail SHALL display issue-level start prerequisites from API-provided `prerequisites` data, including whether each prerequisite issue has been delivered.

#### Scenario: Issue Detail lists prerequisite issues
- **WHEN** a user opens Issue #201
- **AND** Issue #201 has prerequisite issues #200 and #199
- **THEN** Issue Detail shows #200 and #199 as start prerequisites
- **AND** each prerequisite row indicates whether that prerequisite issue is delivered or waiting for delivery

#### Scenario: Issue Detail does not parse body text for prerequisites
- **WHEN** Issue Detail renders start prerequisite information
- **THEN** it SHALL use structured API fields such as `prerequisites` and `startEligibility`
- **AND** it SHALL NOT infer start prerequisites by parsing the Issue description

### Requirement: Web UI cards show waiting for delivery reason

Issue list/card surfaces SHALL show a concise waiting-for-delivery reason when server-provided start eligibility reports that prerequisite issues are not delivered.

#### Scenario: Card shows waiting reason
- **WHEN** an issue card renders Issue #201
- **AND** `startEligibility.waitingForDelivery` contains Issue #200
- **THEN** the card shows a concise reason equivalent to `Waiting for #200`
- **AND** the card does not present the Issue as failed solely because it is waiting for prerequisite delivery

### Requirement: Web UI Start control respects server start eligibility

The Web UI Start control SHALL use server-provided start eligibility to explain when an Issue is waiting for prerequisite delivery, and SHALL rely on the same Server API start guard when start is attempted.

#### Scenario: Start control explains waiting for delivery
- **WHEN** Issue Detail renders Issue #201
- **AND** Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the Start control is disabled or otherwise prevented from starting immediately
- **AND** the page explains that Issue #201 is waiting for #200

#### Scenario: Start attempt surfaces server rejection
- **WHEN** a user attempts to start Issue #201 from the Web UI
- **AND** the Server API rejects the request because Issue #201 is waiting for prerequisite Issue #200 to be delivered
- **THEN** the Web UI shows the actionable server message
- **AND** it does not show that an agent session or pipeline run started

### Requirement: Web UI supports minimal prerequisite declaration

The Web UI SHALL provide the minimum interaction needed to declare that one Issue has a prerequisite issue before start, without introducing a broader graph management interface.

#### Scenario: Declare prerequisite from Issue Detail
- **WHEN** a user declares from Issue #201 that prerequisite Issue #200 must be delivered before start
- **THEN** the Web UI sends a structured API request
- **AND** Issue Detail refreshes to show Issue #200 as a start prerequisite

#### Scenario: Circular declaration error is visible
- **WHEN** the API rejects a Web UI prerequisite declaration with reason `circular-prerequisite`
- **THEN** the Web UI shows a clear validation message
- **AND** it does not add the rejected prerequisite to the displayed list
