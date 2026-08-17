### Requirement: Failed and timed-out lanes remain recoverable
A failed or timed-out verification lane SHALL remain a recoverable lane outcome. Recovery MUST retain every earlier lane result with a durable `pass` outcome and MUST preserve the failed or timed-out outcome and its diagnostics for reporting. Recovery SHALL NOT reset the whole verification sequence or discard completed evidence.

#### Scenario: Recovery preserves earlier passing evidence
- **WHEN** the second lane times out after the first lane has passed
- **THEN** the workflow retains the first lane's durable pass
- **AND** the second lane remains durably identified as timed out with its timeout details
- **AND** recovery can be requested without restarting the first lane

#### Scenario: A failed lane can be repaired without losing diagnostics
- **WHEN** a lane fails with command output or an execution error and its recovery work is started
- **THEN** the original failed result remains observable for the run
- **AND** the recovery attempt is recorded as a new attempt for the same lane
- **AND** the recovery does not mark the lane passed until the lane command succeeds

### Requirement: Recovery resumes at the first lane without a durable pass
When verification recovery starts, the system SHALL select the first lane in the declared order whose current result is not a durable `pass`. It SHALL resume from that lane, SHALL not rerun earlier passing lanes, and SHALL continue to enforce the lane order. A lane after the recovery target SHALL remain unstarted until the target and all preceding required lanes pass.

#### Scenario: Recovery resumes after a timeout
- **WHEN** lanes one and two have passed, lane three has timed out, and lanes four through six have not started
- **THEN** recovery starts lane three
- **AND** it does not execute lanes one or two again
- **AND** it does not execute lanes four through six before lane three passes

#### Scenario: Recovery does not skip a failed lane
- **WHEN** a lane has a durable failure and a later lane could be run independently
- **THEN** recovery targets the failed lane first
- **AND** the later lane remains pending
- **AND** a successful later result cannot open the build-stage gate while the failed lane lacks a durable pass

### Requirement: Runner timeout and result reporting are lane-scoped
The Runner SHALL enforce the budget and report the terminal result using the lane's stable workflow work identity. A lane timeout SHALL be reported as a recoverable timeout outcome rather than as runner loss or as a failure of previously completed lanes. If durable result persistence or result delivery is temporarily unavailable after a lane returns, the Runner MUST retain the exact lane result, MUST retry persistence or delivery, and MUST NOT execute that lane again while the result is held.

#### Scenario: A timed-out command is reported as a lane timeout
- **WHEN** a lane command exceeds its own budget and the Runner terminates it
- **THEN** the workflow receives a timeout result for that lane with the lane identity and configured budget
- **AND** the workflow retains all earlier lane results
- **AND** the lane remains eligible for ordered recovery

#### Scenario: A report failure does not cause duplicate lane execution
- **WHEN** a lane has returned a terminal result but the Runner cannot immediately persist or deliver that result
- **THEN** the Runner retains the exact result and keeps the lane execution fenced
- **AND** a later control-plane retry persists or delivers the same result
- **AND** the Runner does not execute the same lane work item a second time

### Requirement: Replayed recovery is idempotent across downstream effects
The system SHALL give each lane recovery attempt and each downstream workflow task a durable identity. Replaying the same recovery request, stale lane report, or already-acknowledged result MUST reconcile with the existing state rather than create a second active lane execution or a second terminal lane result. Recovery SHALL NOT duplicate `push`, review, or merge side effects; those effects SHALL remain eligible only through their existing downstream workflow order after the verification gate passes.

#### Scenario: Repeating the same recovery does not duplicate downstream tasks
- **WHEN** the same failed-lane recovery is submitted more than once before or after the lane passes
- **THEN** the workflow retains one ordered recovery outcome for that recovery identity
- **AND** it does not enqueue or execute duplicate `push`, review, or merge tasks
- **AND** downstream work remains blocked until all required lanes have durable passes

#### Scenario: A stale report after recovery is harmless
- **WHEN** a late report from a timed-out or failed lane arrives after its recovery attempt has already produced the authoritative durable result
- **THEN** the workflow does not overwrite a newer lane outcome with the stale report
- **AND** it does not reopen completed lanes or repeat downstream side effects
- **AND** the persisted lane and workflow projections continue to show one authoritative ordered state

#### Scenario: Downstream side effects run once after verification
- **WHEN** all required lanes pass and the built-in workflow reaches its existing push, review, or merge tasks
- **THEN** each downstream side effect has one durable completion for its workflow task identity
- **AND** a repeated recovery or duplicate lane result does not invoke that side effect again
