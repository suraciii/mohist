### Requirement: Offline duration past the retention window is detected

Mohist SHALL track how long the `mohist-slack` adapter for a Connection has been unreachable relative to Slack's finite event retention window. When the adapter has been offline long enough that Slack may have discarded events it tried to deliver during the outage, Mohist SHALL record that a possible ingress gap exists for that Connection, rather than assuming every event was retained for replay.

#### Scenario: Short outage does not imply a gap
- **WHEN** the adapter was unreachable for a duration shorter than the Slack event retention window and then reconnects
- **THEN** Mohist does not record a possible-gap condition for the Connection

#### Scenario: Outage beyond the retention window implies a possible gap
- **WHEN** the adapter was unreachable for a duration at or beyond the Slack event retention window and then reconnects
- **THEN** Mohist records a possible-gap condition for the Connection, because events delivered by Slack during the outage may no longer be recoverable

### Requirement: Recovery surfaces a possible-gap notice and asks the user to resend

After the adapter reconnects for a Connection with a recorded possible-gap condition, Mohist SHALL surface a visible notice — on the Connection page, the CLI, and the available Owner diagnostic surface — stating that messages may have been missed during the outage and asking the user to resend any critical delegations. The notice SHALL be honest that Mohist cannot guarantee all events from the outage window were received.

#### Scenario: Owner is told to resend after a long outage
- **WHEN** the adapter reconnects after an outage that exceeded the retention window
- **THEN** the Connection surfaces a possible-gap notice telling the user that some messages may have been missed and that critical delegations should be resent

#### Scenario: No gap notice after a brief reconnect
- **WHEN** the adapter reconnects after an outage that stayed within the retention window
- **THEN** no possible-gap notice is surfaced for the Connection

### Requirement: No automatic replay substitutes for user resend

Mohist SHALL NOT automatically replay or synthesize events to fill a possible ingress gap. The notice is the remedy surfaced to the user; only the user decides which delegations are critical enough to resend. Mohist SHALL NOT claim or imply that all missed messages have been recovered.

#### Scenario: Gap is surfaced, not auto-filled
- **WHEN** a possible-gap condition exists for a Connection
- **THEN** Mohist surfaces the resend notice and does not fabricate, replay, or claim to have recovered the events that may have been missed during the outage
