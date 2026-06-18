## MODIFIED Requirements

### Requirement: Waiting for delivery is not a failure state

The system SHALL represent an Issue waiting for prerequisite delivery as a derived start-readiness blocker on the Issue itself (a `WaitingFor(Issue)` non-startable state), not as `blocked` status, agent failure, session failure, or workflow stage failure.

#### Scenario: Waiting issue remains normal backlog work

- **WHEN** Issue #201 is waiting for prerequisite issue #200 to be delivered
- **THEN** Issue #201 SHALL remain visible as an Issue that has not started
- **AND** Issue #201 SHALL NOT be assigned `status=blocked` solely because of the waiting start prerequisite
- **AND** no agent/session failure SHALL be created for Issue #201

## REMOVED Requirements

### Requirement: Start eligibility summarizes whether an Issue may enter the pipeline

**Reason**: The `IssueStartEligibility` concept is retired. "Can this start?" and "what's blocking it?" are now derived facts owned by the Issue itself (`CanStart` / `Blocker`), not a separate eligibility calculator type with a stringly-typed `Reason` and a redundant `Startable` bool. Keeping a prerequisite-only eligibility summary would re-introduce the anemic-model split this change removes.

**Migration**: Start-readiness is now specified by the `issue-start-readiness` capability. The "Issue is waiting for one prerequisite issue" and "Issue becomes startable after delivery" behaviors are covered there as the `WaitingFor(Issue)` blocker case and the `CanStart` derivation, extended to also cover the new `Draft` blocker. API consumers read the derived `canStart` / `blocker` fields instead of a `startEligibility` object.
