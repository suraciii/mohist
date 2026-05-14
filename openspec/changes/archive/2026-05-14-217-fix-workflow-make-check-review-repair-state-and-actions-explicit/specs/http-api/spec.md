## ADDED Requirements

### Requirement: check-review-repair-state

`GET /api/issues/:number/stage-state` SHALL expose structured Check review repair state when Check review repair evidence exists. The structured state SHALL include attempts used, attempts max, attempts remaining, repair availability, last repair task, last repair status, follow-up review status, stop reason, and unresolved review summary when available.

#### Scenario: Failed review exposes repair state

- **WHEN** a client requests stage-state for an issue whose Check `review-passed` gate failed
- **THEN** the Check stage response SHALL include `checkRepair`
- **AND** `checkRepair` SHALL include attempts used, attempts max, attempts remaining, and whether repair is available
- **AND** `blockedReason` SHALL remain concise rather than becoming the only source of repair details

#### Scenario: Repair completion remains separate from review verdict

- **WHEN** `fix-review-findings` completed
- **AND** the subsequent `review-passed` check failed
- **THEN** `checkRepair.lastRepairStatus` SHALL indicate the repair completed
- **AND** `checkRepair.followUpReviewStatus` SHALL indicate the follow-up review failed
- **AND** the API SHALL NOT represent the completed repair task as review success

#### Scenario: Exhaustion is explicit

- **WHEN** the Check review repair budget is exhausted
- **THEN** `checkRepair.attemptsRemaining` SHALL be `0`
- **AND** `checkRepair.repairAvailable` SHALL be `false`
- **AND** `checkRepair.stopReason` SHALL explain that the maximum repair attempts were reached

### Requirement: check-review-recovery-actions

The issue recovery API SHALL preserve distinct user intents for Check review failures: retry checkpoint recovery, rerun review-only verification, and fix review findings. Repair actions SHALL NOT be hidden behind ambiguous checkpoint retry behavior.

#### Scenario: Retry checkpoint does not schedule exhausted repair

- **WHEN** a client retries a Check review failure after repair budget is exhausted
- **THEN** the API SHALL treat the request as checkpoint recovery
- **AND** it SHALL NOT schedule another `fix-review-findings` task
- **AND** response wording SHALL NOT imply that a new repair attempt was started

#### Scenario: Rerun review only is distinct from repair

- **WHEN** a client requests review-only rerun for a Check review failure
- **THEN** the API SHALL rerun or invalidate review verification work without appending `fix-review-findings`
- **AND** the response SHALL describe the action as review rerun rather than repair

#### Scenario: Fix review findings is explicit and bounded

- **WHEN** repair is available for a Check review failure
- **AND** a client requests fixing review findings
- **THEN** the API SHALL schedule or reuse `fix-review-findings`
- **AND** repeated requests while repair is pending or running SHALL be idempotent
- **AND** the API SHALL reject or clearly explain requests that exceed the automatic repair budget
