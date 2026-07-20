### Requirement: Sticky identity strip is hidden while the outer header is on screen

The sticky identity strip (`data-testid="session-sticky-title"`) inside the transcript scroll container SHALL NOT occupy any visible space while the outer session header (`data-testid="session-header"`) is fully or partially visible inside the transcript scroll viewport. While the outer header remains visible, the sticky strip SHALL be hidden from view (for example by not rendering it, by marking it `inert`, or by keeping it `visibility: hidden` / `display: none`) and SHALL NOT contribute to scroll layout. The outer header is the single source of identity (session name + status) on the first screen.

#### Scenario: Sticky strip is hidden on initial render when header is on screen
- **WHEN** the session page renders with the transcript at its initial scroll position and the outer header fully visible in the scroll container
- **THEN** the sticky identity strip SHALL NOT be visible to the user
- **AND** the session name + status SHALL appear exactly once on the visible portion of the page (in the outer header)

#### Scenario: Hidden sticky strip does not affect scroll layout
- **WHEN** the sticky identity strip is hidden because the outer header is on screen
- **THEN** the strip SHALL NOT contribute height to the scroll container
- **AND** the first row of transcript content SHALL sit directly beneath the outer header with no gap reserved for the hidden strip

### Requirement: Sticky identity strip engages after the outer header scrolls out

Once the outer session header scrolls fully out of the transcript scroll container's visible area (the header's bottom edge passes the top of the scroll container), the sticky identity strip SHALL become visible and SHALL pin to the top of the scroll container as a sticky element. While the outer header remains off-screen, the sticky strip SHALL continue to be visible and SHALL carry only the session name, status badge, and turn count — no other metadata.

#### Scenario: Sticky strip appears when header scrolls out
- **WHEN** the user scrolls the transcript container such that the bottom edge of the outer header passes the top of the scroll container
- **THEN** the sticky identity strip SHALL become visible
- **AND** SHALL pin to the top of the scroll container

#### Scenario: Sticky strip hides again when header re-enters the viewport
- **WHEN** the outer header scrolls back into the visible area of the scroll container (for example the user scrolls back to the top)
- **THEN** the sticky identity strip SHALL stop being visible again
- **AND** SHALL NOT occupy layout space until the header scrolls out again

### Requirement: Sticky strip carries only session name, status, and turn count

The sticky identity strip SHALL display exactly three pieces of information: the session name, the status badge, and the turn count. The strip SHALL NOT display any other metadata that already appears in the outer header (for example stage, model, last activity time, total duration, session id, changed-file count, or any action control). This applies regardless of the viewport width.

#### Scenario: Sticky strip contains only name, status, and turns
- **WHEN** the sticky identity strip is visible (header is off-screen)
- **THEN** the strip's visible text content SHALL include the session name, the status label, and the turn count
- **AND** SHALL NOT include any of: stage chip, model name, last activity time, total duration, session id, changed-file count, or action buttons

### Requirement: Sticky strip remains pinned across further scrolling

While the outer header is off-screen, the sticky identity strip SHALL continue to remain pinned to the top of the scroll container regardless of further scrolling within the transcript. The strip SHALL NOT detach from the top of the scroll container while the header is off-screen.

#### Scenario: Sticky strip stays at the top during continued scrolling
- **WHEN** the outer header is off-screen and the sticky strip is visible
- **AND** the user scrolls further down into the transcript
- **THEN** the sticky strip SHALL remain pinned to the top of the scroll container at all times
- **AND** SHALL NOT be pushed off-screen by the scrolling content
