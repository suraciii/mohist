### Requirement: A confirmed Cancel settles AgentSession activity to idle

A confirmed Cancel that stops the current physical turn SHALL settle the owning AgentSession's activity to `idle`. The settle SHALL be binding-guarded: the stop fact SHALL apply only to the AgentSession's current Runtime Session binding, and a fact reported against a binding that has since been superseded SHALL be ignored. The settle SHALL be produced through the normal cancel API and CLI path — by the Runner reporting the confirmed-stopped fact back to the control plane over the durable channel — and SHALL NOT require an operator to write an internal runtime event. After the confirmed Cancel, the AgentSession SHALL be observable as `idle` through its normal read API and CLI without any manual state repair.

#### Scenario: A confirmed Cancel makes an active session idle

- **WHEN** a Cancel is confirmed (the bound runtime reports the current turn was stopped) against an AgentSession whose activity is `active`
- **THEN** the AgentSession activity SHALL transition to `idle`
- **AND** the next read of the session through its normal API SHALL report `idle`

#### Scenario: A confirmed Cancel makes an unknown session idle

- **WHEN** a Cancel is confirmed against an AgentSession whose activity is `unknown` (for example, after a Runner restart) and the stopped binding is still the current binding
- **THEN** the AgentSession activity SHALL settle to `idle`
- **AND** the next read of the session through its normal API SHALL report `idle`

#### Scenario: A confirmed Cancel does not require an operator-written runtime event

- **WHEN** an operator or workflow issues a Cancel that the owning Runner confirms stopped
- **THEN** the AgentSession SHALL become observable as `idle` through its normal API and CLI
- **AND** SHALL NOT require any operator-injected internal runtime event or manual activity repair

#### Scenario: A stop fact for a superseded binding is ignored

- **WHEN** a confirmed-stopped fact arrives for a Runtime Session binding that is no longer the AgentSession's current binding
- **THEN** the AgentSession activity SHALL NOT change on that fact
- **AND** the current binding, activity, transcript, and accumulated usage SHALL remain unchanged

### Requirement: unknown is never treated as idle without authoritative binding evidence

AgentSession activity `unknown` SHALL settle to `idle` only when authoritative evidence confirms the unchanged current binding has no active turn. `unknown` SHALL NEVER be simplified to `idle` by treating uncertainty as safety. The sanctioned `unknown + runtime evidence -> idle` transition SHALL be produced by reconnect reconciliation (or by an equivalent authoritative runtime fact about the current binding), not only by incidental runtime events or manual operator repair.

#### Scenario: unknown settles to idle only with current-binding evidence

- **WHEN** an AgentSession is `unknown` and authoritative evidence confirms its current Runtime Session binding has no active turn
- **THEN** the AgentSession activity SHALL settle to `idle`
- **AND** the current binding SHALL remain unchanged

#### Scenario: unknown without authoritative evidence stays unknown

- **WHEN** an AgentSession is `unknown` and no authoritative evidence about its current binding is available
- **THEN** the AgentSession activity SHALL remain `unknown`
- **AND** SHALL NOT be treated as `idle`

### Requirement: Cancel never replaces the binding or replays input

A confirmed Cancel SHALL interrupt only the current turn. Cancel SHALL NOT replace the current Runtime Session binding, SHALL NOT create a candidate Runtime Session, and SHALL NOT replay or re-submit the triggering input. A Cancel whose stop cannot be confirmed SHALL surface that uncertainty (for example, an unconfirmed-interrupt flag) rather than reporting a safe idle.

#### Scenario: Cancel preserves the binding

- **WHEN** a confirmed Cancel stops the current turn of a Runtime Session that is still queryable on its owning Runner
- **THEN** the current Runtime Session binding SHALL remain unchanged
- **AND** no replacement candidate Session SHALL be created

#### Scenario: An unconfirmed stop is not reported as idle

- **WHEN** a Cancel request returns but the owning Runner cannot confirm the turn was actually stopped
- **THEN** the cancel path SHALL surface the unconfirmed interruption honestly
- **AND** SHALL NOT settle the AgentSession activity to `idle` on the unconfirmed fact
