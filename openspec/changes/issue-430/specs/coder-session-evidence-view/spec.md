### Requirement: Session summary is compact and non-duplicative

The Session page SHALL present the session name, current status, stage, model, turn count, timestamps, duration, and other available summary metadata in one compact primary header. On viewports wide enough to present the summary inline, the summary SHALL remain a single visual row; on narrower viewports it MUST remain compact, avoid horizontal overflow, and preserve the session name and status as the highest-priority information. The page SHALL NOT place an always-visible second copy of the session name and status between the primary header and the transcript.

#### Scenario: Workflow session opens with a compact summary

- **WHEN** a user opens a workflow-bound Session page with session name, status, stage, model, turn count, time, and duration data
- **THEN** the primary header SHALL present the available summary data compactly
- **AND** the session name and status SHALL each appear only once before the transcript begins
- **AND** transcript content SHALL be visible in the first viewport when transcript content exists

#### Scenario: Generic session uses the same information hierarchy

- **WHEN** a user opens a generic Session page
- **THEN** the page SHALL use the same compact primary-header and transcript hierarchy
- **AND** it SHALL omit workflow-only metadata that is unavailable rather than fabricating it

#### Scenario: Compact summary fits a narrow viewport

- **WHEN** the Session page is displayed on a narrow viewport
- **THEN** the primary summary SHALL NOT cause horizontal page overflow
- **AND** the session name and current status SHALL remain readable
- **AND** lower-priority metadata MUST truncate, collapse, or move to an accessible overflow presentation before displacing the transcript from the first viewport

### Requirement: Sticky identity appears only after the primary header leaves view

The Session page SHALL provide a compact sticky identity bar containing only the session name and current status while the user reads a scrolled transcript. The sticky identity bar SHALL remain hidden while the primary header is visible and SHALL become visible only after the primary header has scrolled out of view. It SHALL cease duplicating the primary header when the user returns to the top.

#### Scenario: Sticky identity is absent at the top of the page

- **WHEN** the primary session header is visible
- **THEN** the sticky identity bar SHALL be hidden
- **AND** the session name and status SHALL NOT be repeated in a persistent transcript row

#### Scenario: Sticky identity appears after scrolling past the header

- **WHEN** the user scrolls the transcript until the primary header is no longer visible
- **THEN** a sticky identity bar SHALL become visible
- **AND** it SHALL show the session name and current status
- **AND** it SHALL NOT repeat turn count, model, usage, navigation, or session actions

#### Scenario: Sticky identity updates with live status

- **WHEN** the sticky identity bar is visible and the session status changes
- **THEN** the sticky identity bar SHALL show the new current status without requiring navigation or a page reload

### Requirement: Sibling navigation has one responsive presentation per viewport

For workflow-bound sessions, the Session page SHALL expose sibling-session navigation in exactly one place at a time. A viewport that displays the status-rich sibling sidebar SHALL NOT also display previous/next sibling controls. A viewport that cannot display the sidebar SHALL provide compact previous/next navigation instead. Both presentations SHALL navigate within the same workflow run's ordered sibling set and SHALL preserve the current-session indication and sibling status information already provided by the sidebar.

#### Scenario: Wide viewport uses the sibling sidebar only

- **WHEN** a workflow-bound Session page has sibling sessions and the viewport displays the sibling sidebar
- **THEN** the sidebar SHALL list the sibling sessions with their existing status and current-session indication
- **AND** previous/next sibling controls SHALL NOT be displayed elsewhere on the page

#### Scenario: Narrow viewport uses previous and next controls only

- **WHEN** a workflow-bound Session page has sibling sessions and the viewport does not display the sibling sidebar
- **THEN** compact previous/next controls SHALL be available
- **AND** the sibling sidebar SHALL NOT be displayed
- **AND** activating a control SHALL navigate only to the adjacent session in the same workflow run

#### Scenario: Generic session has no fabricated sibling navigation

- **WHEN** a generic session has no workflow sibling set
- **THEN** the page SHALL NOT display a sibling sidebar or previous/next sibling controls

### Requirement: Session actions communicate priority and unavailability

The Session page SHALL present dangerous or infrequent actions, including Cancel session, with lower visual priority than session identity, status, transcript content, and ordinary navigation. Cancel SHALL retain its confirmation step. Compact, Reset, and any other displayed but unavailable session action MUST expose a clear reason for its unavailability through an affordance available to pointer and keyboard users.

#### Scenario: Running session offers secondary cancellation

- **WHEN** a running session can be cancelled
- **THEN** Cancel session SHALL remain available without being presented as the page's primary action
- **AND** activating it SHALL require confirmation before cancellation is requested

#### Scenario: Recovery actions are unavailable while active

- **WHEN** Compact or Reset is displayed but unavailable because the session is active
- **THEN** the action SHALL be disabled
- **AND** pointer hover and keyboard focus SHALL expose an explanation that the action is unavailable while the session is active

#### Scenario: Action is unavailable while another request is pending

- **WHEN** a displayed session action is disabled because another session action is pending
- **THEN** an accessible explanation SHALL identify the pending operation as the reason
- **AND** the disabled action SHALL NOT submit a second request

### Requirement: Session identity and ended time are directly usable

The primary header SHALL provide the complete stable session ID as a one-activation copy target and SHALL provide accessible confirmation after a successful copy. For an ended session whose end occurred on a calendar date earlier than the user's current local date, the primary time display SHALL use an absolute local date and time; its relative age SHALL remain available as supplementary hover or focus information. Every relative timestamp SHALL expose its exact timestamp as supplementary hover or focus information.

#### Scenario: User copies the complete session ID

- **WHEN** the primary header has a session ID and the user activates its copy target
- **THEN** the complete session ID SHALL be written to the clipboard even if its visible label is abbreviated
- **AND** the page SHALL provide accessible confirmation that the ID was copied

#### Scenario: Session ended on an earlier local date

- **WHEN** a terminal session's completion time falls on a calendar date earlier than the user's current local date
- **THEN** the primary header SHALL display the completion time as an absolute local date and time
- **AND** the relative age SHALL be available on hover or keyboard focus

#### Scenario: Recent session time remains interpretable

- **WHEN** a session timestamp is displayed as a relative value
- **THEN** its exact local date and time SHALL be available on hover or keyboard focus

### Requirement: Followup composer reflects the actual interaction state

The followup composer SHALL communicate whether the current session can accept input, whether a submitted followup is queued or being sent, or whether the session has ended and cannot accept another message. Its controls and explanatory text MUST agree with the session's actual followup eligibility, and the composer SHALL prevent duplicate submission while a followup is pending.

#### Scenario: Session is ready for followup input

- **WHEN** the current session is eligible to accept a followup
- **THEN** the composer SHALL present an enabled input with text indicating that a followup can be sent
- **AND** a non-empty followup SHALL be submit-capable

#### Scenario: Followup is queued or sending

- **WHEN** a followup submission is pending
- **THEN** the composer SHALL communicate that the message is queued or being sent
- **AND** the input and send action SHALL prevent another submission until the pending submission resolves

#### Scenario: Session has ended

- **WHEN** the session is in a terminal state
- **THEN** the composer region SHALL communicate that the session has ended and cannot accept another message
- **AND** it SHALL NOT provide an enabled followup input or send action

### Requirement: Existing evidence, navigation context, and contracts are preserved

The information-frame change SHALL apply to both workflow-bound and generic Session detail pages while preserving transcript and tool-call rendering, error evidence, usage and context-health evidence, recovery and lineage behavior, sibling-sidebar entry content, back and workflow-context navigation, and the existing semantics of followup, cancel, Compact, and Reset actions. It SHALL consume the existing session, transcript, usage, and sibling data and SHALL NOT require new or changed server APIs, DTO contracts, persistence, transcript recording, or subscription behavior.

#### Scenario: Existing transcript evidence remains unchanged

- **WHEN** a Session page is rendered with transcript turns, tool calls, errors, usage, context health, or lineage evidence
- **THEN** that evidence SHALL remain available with its existing content and behavior
- **AND** reorganizing the page frame SHALL NOT alter transcript rendering or recording

#### Scenario: Existing session action semantics remain unchanged

- **WHEN** followup, cancel, Compact, or Reset is eligible under the existing session state
- **THEN** the action SHALL retain its existing eligibility, confirmation, and request semantics
- **AND** visual reprioritization or explanatory text SHALL NOT broaden or narrow that eligibility

#### Scenario: Existing projections satisfy the page

- **WHEN** the compact Session page frame is loaded
- **THEN** it SHALL use the existing session, transcript, usage, context-health, lineage, and sibling projections
- **AND** no new or changed server API, DTO, persistence, recording, or subscription contract SHALL be required
