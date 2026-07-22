### Requirement: Rail card headers render untruncated
Rail card headers on the issue detail page MUST render their title in full at desktop rail width. A title such as "Configuration" MUST NOT be truncated to a fragment (for example "CONF…").

#### Scenario: Configuration rail card header shows the full title at desktop width
- **WHEN** the reference rail is rendered on a desktop (non-narrow) viewport
- **THEN** the Configuration rail card header MUST display the full word "Configuration" without truncation

### Requirement: Desktop reference rail stays in view while scrolling
On a desktop (non-narrow) viewport, the reference rail MUST remain visible while the main reading-flow content scrolls. The rail MUST NOT scroll out of view on long pages.

#### Scenario: Reference rail remains visible while scrolling a long page
- **WHEN** a long issue page is scrolled on a desktop viewport
- **THEN** the reference rail cards (Details, Configuration, and Actions) MUST remain visible alongside the scrolling content

#### Scenario: Narrow viewport layout is unaffected
- **WHEN** the issue detail page is rendered on a narrow viewport
- **THEN** the rail stacks and collapses per the existing narrow-viewport behavior and is not required to remain fixed
