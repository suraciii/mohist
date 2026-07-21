### Requirement: Workflow turns eventually produce complete AgentSession transcripts

For every Workflow-source OpenCode turn with an associated AgentSession, the system SHALL eventually persist and expose the submitted user input and the normalized runtime events accepted for that turn when transient delivery failures recover. The transcript SHALL preserve assistant text, assistant reasoning, tool-call lifecycle, usage, resolved model observations, and events supplied by final-response reconciliation in production order, with the submitted input before all dependent activity.

#### Scenario: Transcript event succeeds after a transient failure

- **WHEN** a Workflow turn produces input and assistant text but the assistant event's first upload fails
- **THEN** the input and assistant event SHALL remain eligible for ordered delivery
- **AND** after the server accepts the retried event, the transcript SHALL expose the input followed by the assistant text

#### Scenario: Pending transcript events survive restart

- **WHEN** the runner restarts with locally committed but Server-unaccepted reasoning, tool, usage, or model events from a Workflow turn
- **THEN** it SHALL resume delivery of those original events
- **AND** accepted events SHALL appear in the associated Workflow AgentSession transcript in their original order

#### Scenario: Final-response reconciliation follows live events

- **WHEN** final-response reconciliation supplies normalized content missing from the live event stream while earlier live events are pending
- **THEN** the reconciled events SHALL be retained after the earlier live events
- **AND** delivery recovery MUST NOT reorder the reconciled content ahead of those events

### Requirement: Retried inputs preserve Workflow transcript boundaries

A Workflow turn's `session.input` MUST be positively accepted before any dependent event from that turn can be delivered. When multiple turns reuse the same logical AgentSession, pending delivery and retry SHALL preserve each input and its activity as a distinct turn, including across runner restart. A later turn's input MUST NOT overtake unaccepted events from an earlier turn or become part of the earlier transcript boundary.

#### Scenario: Initial input is temporarily unaccepted

- **WHEN** a Workflow turn's input receives no acceptance receipt and the turn produces assistant and terminal events
- **THEN** the runner SHALL retain the input and its dependent events in order
- **AND** MUST NOT deliver the dependent events until that input is positively accepted

#### Scenario: Back-to-back turns recover without merging

- **WHEN** two Workflow turns reuse one AgentSession and the first turn still has pending events when the second input is produced
- **THEN** recovery SHALL preserve the complete first sequence before the second input
- **AND** the transcript SHALL expose two distinct turns with their own inputs and activity

#### Scenario: Stale binding cannot receive transcript content

- **WHEN** a pending Workflow transcript event carries a physical runtime session identity that no longer matches the AgentSession's current binding
- **THEN** the event MUST NOT be attached to the current runtime session or a different transcript turn

### Requirement: Transcript delivery failure is independent of the Workflow result

A Workflow transcript Server-upload failure MUST be observable for diagnosis, but MUST NOT prevent the OpenCode prompt from running after its input is locally durable, change a successful runtime result to failed, replace or obscure the original runtime failure, or delay returning the Workflow result until Server delivery succeeds. The locally committed transcript events SHALL remain pending for later delivery. The reporter MUST wait for every observed event's local enqueue attempt to settle before returning the Workflow result; failed produced-fact writes SHALL remain in memory for autonomous snapshot recovery without replacing that result.

#### Scenario: Initial input upload fails while the turn succeeds

- **WHEN** the initial `session.input` is locally durable, its Server upload fails, and the OpenCode turn succeeds
- **THEN** the Workflow turn SHALL return its successful runtime result without waiting for a successful retry
- **AND** the input and subsequent turn events SHALL remain pending in production order

#### Scenario: Runtime and transcript delivery both fail

- **WHEN** an OpenCode turn fails and one or more transcript uploads also fail
- **THEN** the Workflow result SHALL preserve the original OpenCode failure
- **AND** the upload failures SHALL remain independently observable and retryable
