## ADDED Requirements

### Requirement: Issue start prerequisites are explicit issue-level facts

The system SHALL allow a user to declare that one Issue has a start prerequisite requiring a prerequisite issue to be delivered before the current Issue may start. Issue-level start prerequisites SHALL be separate from task-level `tasks.json` `dependsOn` fields.

#### Scenario: Declare prerequisite issue

- **WHEN** a user declares that Issue #201 requires Issue #200 to be delivered before start
- **THEN** Issue #201 SHALL record Issue #200 as a prerequisite issue
- **AND** Issue #201 SHALL expose that start prerequisite in structured Issue data

#### Scenario: Task dependsOn remains separate

- **WHEN** an Issue has issue-level start prerequisites
- **THEN** the system SHALL NOT copy them into `tasks.json` task `dependsOn`
- **AND** task-level `dependsOn` SHALL NOT be interpreted as an issue-level start prerequisite

### Requirement: Prerequisite issue delivery is evaluated from issue lifecycle state

The system SHALL evaluate a prerequisite issue as delivered only when the prerequisite issue has `stage=done`, `status=completed`, and `mergeState=merged`.

#### Scenario: Delivered prerequisite satisfies start prerequisite

- **WHEN** Issue #201 has prerequisite issue #200
- **AND** Issue #200 has `stage=done`, `status=completed`, and `mergeState=merged`
- **THEN** prerequisite issue #200 SHALL be reported as delivered for Issue #201

#### Scenario: Done without merge is not delivered

- **WHEN** Issue #201 has prerequisite issue #200
- **AND** Issue #200 has `stage=done` and `status=completed`
- **AND** Issue #200 does not have `mergeState=merged`
- **THEN** prerequisite issue #200 SHALL be reported as waiting for delivery for Issue #201

### Requirement: Start eligibility summarizes whether an Issue may enter the pipeline

The system SHALL compute start eligibility for an Issue from its startable workflow state and all declared start prerequisites. If any prerequisite issue is not delivered, start eligibility SHALL be not startable and SHALL identify the prerequisite issue or issues waiting for delivery.

#### Scenario: Issue is waiting for one prerequisite issue

- **WHEN** Issue #201 has prerequisite issue #200
- **AND** Issue #200 is not delivered
- **THEN** Issue #201 start eligibility SHALL be not startable
- **AND** Issue #201 SHALL expose `waitingForDelivery` containing Issue #200
- **AND** the user-facing reason SHALL be equivalent to `Waiting for #200`

#### Scenario: Issue becomes startable after delivery

- **WHEN** Issue #201 has prerequisite issue #200
- **AND** Issue #200 becomes delivered
- **THEN** Issue #201 start eligibility SHALL no longer wait for Issue #200
- **AND** no manual prerequisite cleanup SHALL be required
- **AND** the system SHALL NOT auto-start Issue #201

### Requirement: Start prerequisite declarations reject circular relationships

The system SHALL reject a start prerequisite declaration that would make an Issue directly or indirectly require itself before start.

#### Scenario: Direct circular prerequisite rejected

- **WHEN** a user declares that Issue #200 requires Issue #200 before start
- **THEN** the declaration SHALL be rejected
- **AND** Issue #200 SHALL NOT record that start prerequisite

#### Scenario: Indirect circular prerequisite rejected

- **WHEN** Issue #201 already requires Issue #200 before start
- **AND** a user declares that Issue #200 requires Issue #201 before start
- **THEN** the declaration SHALL be rejected with reason `circular-prerequisite`
- **AND** Issue #200 SHALL NOT record Issue #201 as a prerequisite issue

### Requirement: Waiting for delivery is not a failure state

The system SHALL represent an Issue waiting for prerequisite delivery as start eligibility state, not as `blocked` status, agent failure, session failure, or workflow stage failure.

#### Scenario: Waiting issue remains normal backlog work

- **WHEN** Issue #201 is waiting for prerequisite issue #200 to be delivered
- **THEN** Issue #201 SHALL remain visible as an Issue that has not started
- **AND** Issue #201 SHALL NOT be assigned `status=blocked` solely because of the waiting start prerequisite
- **AND** no agent/session failure SHALL be created for Issue #201
