### Requirement: The thinking indicator displays an elapsed wait time in a live session

While the hosting session is alive (running) and in the thinking state, the thinking indicator SHALL display the elapsed time since the thinking state began. The displayed elapsed time SHALL update once per second while the session remains in the thinking state. The elapsed time SHALL be formatted using the transcript's existing elapsed-duration format (for example `4.7s` below one minute, `2m 03s` at or above one minute).

#### Scenario: Thinking indicator shows a ticking elapsed time
- **WHEN** the session is running and in the thinking state
- **THEN** the thinking indicator SHALL display the elapsed time since the thinking state began
- **AND** the displayed elapsed time SHALL advance once per second while the thinking state continues

#### Scenario: Thinking elapsed formats consistently with tool durations
- **WHEN** the thinking elapsed time crosses formatting boundaries (sub-second, seconds, minutes, hours)
- **THEN** the displayed elapsed time SHALL use the same formatting rules as a tool row's elapsed duration

### Requirement: The thinking elapsed display is gated on session liveness and thinking state

The thinking elapsed display SHALL render only while the hosting session is running and in the thinking state. The elapsed display SHALL NOT render in a non-running session, regardless of any thinking flag carried over from earlier. When the session leaves the thinking state (for example, the agent begins streaming visible content) the elapsed display MAY be removed or stop advancing; when the session ends, the elapsed display SHALL be removed.

#### Scenario: Thinking elapsed is gated on session liveness
- **WHEN** the session is not running (ended, completed, failed, cancelled, or inactive) and a thinking flag is set
- **THEN** the thinking indicator and its elapsed display SHALL NOT render

#### Scenario: Thinking elapsed is removed when the session ends
- **WHEN** a running session that is displaying the thinking elapsed time transitions to not running
- **THEN** the thinking indicator and its elapsed display SHALL be removed

#### Scenario: Thinking elapsed stops advancing when the agent leaves the thinking state
- **WHEN** the session transitions out of the thinking state because visible content begins streaming
- **THEN** the thinking elapsed display SHALL stop advancing or be removed
