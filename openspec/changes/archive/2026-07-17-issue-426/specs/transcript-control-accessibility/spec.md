### Requirement: Every transcript interaction control has a readable accessible name

Every interactive control in the transcript — including tool-row, context-group, and diff-summary expand/collapse controls — SHALL expose a readable accessible name to assistive technology. A control's accessible name MUST NOT be "unknown" or empty.

#### Scenario: Expand-collapse control exposes a readable name

- **WHEN** an expand/collapse control for a tool row, context group, or diff summary is rendered
- **THEN** the control exposes a readable accessible name that identifies what it controls (e.g. the tool title or the changed-files summary)

#### Scenario: Accessible name is never unknown

- **WHEN** any transcript interaction control is rendered
- **THEN** its accessible name is a readable, descriptive string and is never "unknown" or empty

### Requirement: Expand-collapse controls expose their expanded/collapsed state

Transcript controls that toggle a disclosed region (tool-row details, context-group contents, diff-summary details) SHALL expose their expanded/collapsed state to assistive technology via `aria-expanded`. The state MUST reflect the actual toggle state.

#### Scenario: Collapsed control reports collapsed state

- **WHEN** a disclosure control is in its collapsed state
- **THEN** the control exposes `aria-expanded="false"`

#### Scenario: Expanded control reports expanded state

- **WHEN** a disclosure control is expanded
- **THEN** the control exposes `aria-expanded="true"`

### Requirement: Decorative icons are hidden from assistive technology

Icons that convey no information beyond the control's text label — tool icons, status dots, running indicators, and chevrons — MUST be hidden from assistive technology (e.g. `aria-hidden="true"`) so they are not announced.

#### Scenario: Decorative icon is not announced

- **WHEN** a control renders a decorative icon (tool icon, status dot, or chevron) alongside a text label
- **THEN** the decorative icon is marked hidden from assistive technology and is not part of the control's accessible name

### Requirement: Streamed transcript content is announced as a live region

The transcript turn list SHALL announce streamed content to assistive technology so new assistant output is perceivable as it arrives. The streaming and thinking activity indicators SHALL be exposed as status/live regions so their appearance and removal are announced.

#### Scenario: New streamed content is announced

- **WHEN** new assistant content streams into the transcript turn list
- **THEN** the turn list is configured as a live region so the new content is announced to assistive technology

#### Scenario: Activity indicator appearance and removal are announced

- **WHEN** a streaming or thinking activity indicator appears or is removed
- **THEN** the change is exposed as a status/live region so assistive technology announces it
