### Requirement: Follow-up user input has consistent activity-state semantics

A follow-up user input SHALL have a single activity-state semantics shared by the server's active-time computation and the web's round rendering. The web's presentation of a follow-up input as a new round and the session's reported active/inactive status SHALL be consistent: the web SHALL NOT present a follow-up input as a new active round while the session is reported inactive, and the session SHALL NOT be reported inactive while a follow-up input is presented as a new active round. (The mechanism — refreshing active time on the server, or not rendering the follow-up as a new round on the web — is a design decision; this requirement states the outcome that must hold regardless of mechanism.)

#### Scenario: A presented follow-up round agrees with active status

- **WHEN** the web presents a follow-up user input as a new round
- **THEN** the session's reported status SHALL be active

#### Scenario: An inactive session does not present a fresh active follow-up round

- **WHEN** the session is reported inactive
- **THEN** the web SHALL NOT present a follow-up user input as a new active round

### Requirement: Recovery invariant is preserved

A follow-up that does not produce runtime output (for example, a rejected or idle follow-up) SHALL NOT extend the session's active window. Immediately after such a follow-up, the session SHALL be reportable as inactive, and Compact/Reset SHALL remain available without waiting for the active window to elapse.

#### Scenario: A rejected follow-up leaves the session inactive

- **WHEN** a follow-up is rejected and the runner emits `session.followup_failed` with no runtime response event
- **THEN** the session's reported status SHALL be inactive

#### Scenario: Compact/Reset remain available after a rejected follow-up

- **WHEN** a follow-up is rejected without producing runtime output
- **THEN** Compact and Reset SHALL remain available for the session immediately, without waiting for the active window to elapse
