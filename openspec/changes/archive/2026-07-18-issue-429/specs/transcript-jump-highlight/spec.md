### Requirement: Locating a transcript row applies a transient highlight to that row

Whenever a transcript row is located programmatically as the destination of a jump (from the mini timeline or from the session-error evidence bar), that row SHALL receive a transient visual highlight distinct from its default styling. The highlight SHALL be applied after the row is scrolled into view so that the reader's eye lands on the destination. A row that is not the target of a jump SHALL NOT carry the highlight.

#### Scenario: A located row receives the highlight
- **WHEN** a jump action resolves a target transcript row and scrolls it into view
- **THEN** that target row SHALL receive the transient highlight

#### Scenario: A non-targeted row does not receive the highlight
- **WHEN** a jump action targets row A and other rows are present in the transcript
- **THEN** only row A SHALL carry the highlight
- **AND** all other rows SHALL NOT carry the highlight

### Requirement: The highlight is short-lived and auto-dismisses

The transient highlight SHALL be time-bounded: it SHALL appear at the moment of location and SHALL remove itself automatically after a bounded duration without further user action. The highlight duration SHALL be driven by an injectable time source so that tests do not depend on wall-clock timing.

#### Scenario: Highlight clears automatically after a bounded duration
- **WHEN** a row receives the transient highlight and no further user action occurs
- **THEN** the highlight SHALL be removed after a bounded duration without any additional user input

#### Scenario: Highlight timing is deterministic in tests
- **WHEN** the highlight is exercised in a unit or spec test
- **THEN** the highlight's appearance and dismissal SHALL be controllable via an injected time source
- **AND** SHALL NOT depend on real wall-clock elapsed time

### Requirement: The highlight is dismissable by the user

A user SHALL be able to dismiss the highlight before its auto-dismiss deadline. Dismissing input SHALL include at minimum an Escape key press and a pointer interaction outside the highlighted row. Initiating a new jump (locating a different row, or re-locating the same row) SHALL move the highlight to the new target rather than stacking a second highlight.

#### Scenario: Escape dismisses the highlight early
- **WHEN** a row carries the transient highlight and the user presses Escape
- **THEN** the highlight SHALL be removed before its auto-dismiss deadline

#### Scenario: Clicking outside the highlighted row dismisses it
- **WHEN** a row carries the transient highlight and the user clicks elsewhere
- **THEN** the highlight SHALL be removed before its auto-dismiss deadline

#### Scenario: A new jump moves the highlight instead of stacking
- **WHEN** a row carries the transient highlight and a new jump targets a different row
- **THEN** the highlight SHALL be removed from the original row
- **AND** SHALL be applied to the newly targeted row
- **AND** at most one row SHALL carry the highlight at any time

### Requirement: The highlight is launcher-agnostic and decorative to assistive tech

The highlight mechanism SHALL be invokable by any jump launcher (mini timeline node, error evidence bar, next-error affordance, or future jump sources) through a single shared locate-and-highlight pathway. The highlight SHALL be a decorative visual cue: it SHALL NOT be announced via `aria-live` or any other polite/assertive live region, and it SHALL NOT change the row's accessibility role or name. Screen-reader users SHALL receive jump feedback through the focus/scroll behavior of the located row, not through the highlight.

#### Scenario: Any launcher can trigger the highlight through the same pathway
- **WHEN** any jump launcher (mini timeline node, error bar, or next-error) targets a row
- **THEN** the row SHALL receive the same transient highlight via the same shared pathway

#### Scenario: Highlight is not announced to assistive tech
- **WHEN** a row receives the transient highlight
- **THEN** the highlight SHALL NOT be conveyed through an `aria-live` region
- **AND** SHALL NOT alter the row's accessibility role or accessible name
