## MODIFIED Requirements

### Requirement: Session page reads as a Mohist-to-Coder transcript

The coder session page SHALL present the session as a read-only Mohist-to-Coder transcript instead of an event log, workflow dashboard, or raw tool viewer.

#### Scenario: Prompt-led turns anchor the transcript

- **WHEN** the session page renders a transcript with one or more Mohist prompts
- **THEN** each Mohist prompt appears as the visible turn boundary
- **AND** assistant output is rendered beneath that prompt as ordered assistant parts

#### Scenario: Internal transcript noise stays out of the primary view

- **WHEN** a transcript includes internal tools, placeholders, or raw payload-first records
- **THEN** the primary transcript hides `todowrite`, stale `unknown` placeholders, and duplicate lifecycle fragments by default
- **AND** raw payloads are only shown in secondary expandable details when needed

#### Scenario: File-changing output belongs to the assistant turn

- **WHEN** the assistant applies patches or edits files during a turn
- **THEN** the turn shows compact file-change summaries and expandable diff details as part of that turn
- **AND** changed files do not appear only as detached workflow cards or summaries
