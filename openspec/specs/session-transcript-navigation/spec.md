### Requirement: Transcript table of contents for turn navigation

The session transcript page SHALL provide a table of contents (TOC) that lists every turn in the session as a navigable entry. Each entry SHALL identify its turn (by index or short label) and SHALL act as an anchor that scrolls the transcript so the target turn becomes visible. The TOC SHALL cover the full set of rendered turns, including turns appended during a running session, and SHALL not require a full page reload to reflect new turns.

#### Scenario: TOC lists every rendered turn

- **WHEN** the transcript renders N turns
- **THEN** the TOC SHALL list N entries, one per turn
- **AND** the entries SHALL appear in the same order as the turns in the transcript

#### Scenario: TOC entry scrolls to its turn

- **WHEN** the user activates the TOC entry for turn K
- **THEN** the transcript scroll container SHALL scroll so turn K is visible in the viewport

#### Scenario: TOC reflects turns added during a running session

- **WHEN** a new turn arrives while the session is running
- **THEN** a new TOC entry for that turn SHALL appear
- **AND** no full page reload SHALL be required for the entry to appear

### Requirement: Keyboard shortcuts for transcript navigation

The session transcript page SHALL support keyboard shortcuts for turn-by-turn and boundary navigation. `j` SHALL move to the next turn, `k` SHALL move to the previous turn, `g` SHALL move to the first turn (top), and `G` (shift+g) SHALL move to the last turn (bottom). These shortcuts SHALL NOT fire while a text input, textarea, or contenteditable element is focused (for example the followup composer), so typing is never hijacked. Shortcut handling SHALL be scoped to the active session transcript page and SHALL NOT affect other pages.

#### Scenario: j moves to the next turn

- **WHEN** no text input is focused and the current turn is turn K
- **AND** the user presses `j`
- **THEN** the transcript SHALL scroll so turn K+1 is visible

#### Scenario: k moves to the previous turn

- **WHEN** no text input is focused and the current turn is turn K greater than 1
- **AND** the user presses `k`
- **THEN** the transcript SHALL scroll so turn K-1 is visible

#### Scenario: g and G move to transcript boundaries

- **WHEN** no text input is focused and the user presses `g`
- **THEN** the transcript SHALL scroll to the first turn
- **WHEN** the user presses `G` (shift+g)
- **THEN** the transcript SHALL scroll to the last turn

#### Scenario: Shortcuts defer to a focused text input

- **WHEN** the followup composer textarea (or any text input) is focused
- **AND** the user presses `j`
- **THEN** the transcript SHALL NOT navigate to another turn
- **AND** the focused input SHALL receive the `j` keystroke as normal text entry

### Requirement: Copy full transcript text action

The session transcript page SHALL provide a "copy full text" action that writes the entire transcript to the clipboard as plain text in a single operation. The action SHALL be available whenever at least one turn is rendered and SHALL copy all turns (prompts and responses) in document order. On success the UI SHALL give positive feedback; on failure (for example a denied clipboard permission) the UI SHALL surface the failure and SHALL NOT silently appear to succeed.

#### Scenario: Copy succeeds for a non-empty transcript

- **WHEN** the transcript has one or more turns
- **AND** the user activates the "copy full text" action
- **THEN** the entire transcript SHALL be written to the clipboard as plain text in document order
- **AND** the UI SHALL show a success indication

#### Scenario: Copy is unavailable for an empty transcript

- **WHEN** the transcript has no turns
- **THEN** the "copy full text" action SHALL be disabled or hidden

#### Scenario: Copy failure is surfaced

- **WHEN** the clipboard write is rejected
- **THEN** the UI SHALL indicate that the copy did not succeed
- **AND** the UI SHALL NOT report success
