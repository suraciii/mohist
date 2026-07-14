### Requirement: Activity distinguishes execution event types, not only session cards

The Activity page SHALL be an event-level execution evidence view. Its entries SHALL distinguish at least these event types by production meaning: issue state changes, workflow stage changes, agent session starts/ends, runner events, and failures. The page SHALL NOT be limited to active/waiting/recent session cards without an event-type identity on each entry. Each entry SHALL carry an identifiable event-type marker (label, icon, or category) so the owner can scan what kind of production-line event occurred, not merely which session is active.

#### Scenario: Issue state change is distinguishable from a session card

- **WHEN** an issue state change event occurs and is shown on the Activity page
- **THEN** the entry SHALL be marked as an issue state change event type
- **AND** it SHALL NOT be rendered as an undifferentiated session card with no event-type identity

#### Scenario: Workflow stage change is distinguishable from other event types

- **WHEN** a workflow stage transition event occurs and is shown on the Activity page
- **THEN** the entry SHALL be marked as a workflow stage change event type
- **AND** it SHALL be visually distinguishable from an issue state change, an agent session event, a runner event, and a failure

#### Scenario: Agent session start or end is distinguishable from runner events

- **WHEN** an agent session starts or ends and a runner heartbeat or connection event occurs
- **THEN** each entry SHALL be marked with its respective event type (agent session event vs runner event)
- **AND** the two event types SHALL NOT collapse into a single undifferentiated feed

#### Scenario: Failure is distinguishable as its own event type

- **WHEN** a failure event occurs and is shown on the Activity page
- **THEN** the entry SHALL be marked as a failure event type
- **AND** it SHALL be distinguishable from a routine workflow or metadata event

### Requirement: Needs-attention and failure events surface above normal low-priority events

The Activity page SHALL surface needs-attention and failure events with greater prominence than normal low-priority routine events. Failures, approvals, and blocked states SHALL appear above routine activity in the page's reading order or in a dedicated attention zone, with visual priority matching the shared status language. Routine low-priority events SHALL NOT occupy equal or greater prominence than needs-attention or failure events on the first screen.

#### Scenario: Failures surface above routine activity

- **WHEN** both a failure event and routine low-priority events exist on the Activity page
- **THEN** the failure event SHALL appear with greater prominence than the routine events
- **AND** the routine events SHALL NOT precede the failure in the page's attention ordering

#### Scenario: Approval and blocked states surface above routine activity

- **WHEN** an approval-required event or a blocked-state event exists alongside routine low-priority events
- **THEN** the approval or blocked event SHALL appear with greater prominence than the routine events
- **AND** its visual priority SHALL match the shared status language (warning/danger tone)

#### Scenario: No needs-attention events yields a normal routine view

- **WHEN** no failure, approval, or blocked events exist
- **THEN** the page SHALL NOT reserve or show an empty attention zone that competes with routine activity

### Requirement: Activity entries can be grouped or filtered by production meaning

The Activity page SHALL allow the owner to group or filter entries by production meaning (event type and/or attention level) so that the owner can isolate a class of events when scanning evidence. Filtering or grouping SHALL operate on the event-type and attention identity that entries already carry, and SHALL NOT require the owner to open a per-issue dialog to reduce the feed.

#### Scenario: Owner filters the feed to a single event type

- **WHEN** the owner activates a filter for one event type (e.g. failures only)
- **THEN** only entries of that event type SHALL be shown
- **AND** entries of other event types SHALL be hidden from the feed

#### Scenario: Owner filters to needs-attention events

- **WHEN** the owner activates a needs-attention filter
- **THEN** only failure, approval, and blocked events SHALL be shown
- **AND** routine low-priority events SHALL be hidden

#### Scenario: Clearing filters restores the full evidence view

- **WHEN** the owner clears an active filter
- **THEN** all event types SHALL be visible again in their attention-ordered prominence

### Requirement: Activity provides orientation-preserving navigation entry points

From the Activity page, the owner SHALL be able to navigate to the relevant issue, the workflow context, the executing session, and runner detail. Activity SHALL provide direct links to sessions, not only to issues, so the owner can follow the execution trail without first opening an issue. Each entry SHALL preserve orientation by linking to a project-scoped destination rather than a context-less root route.

#### Scenario: Entry links to the relevant issue

- **WHEN** an Activity entry is associated with an issue
- **THEN** the entry SHALL link to that issue's detail page
- **AND** the link SHALL be project-scoped

#### Scenario: Entry links directly to the executing session

- **WHEN** an Activity entry is associated with a coder session
- **THEN** the entry SHALL provide a direct link to that session
- **AND** the owner SHALL NOT be required to open the issue first to reach the session

#### Scenario: Entry links to workflow context

- **WHEN** an Activity entry is associated with a workflow run or stage
- **THEN** the entry SHALL provide an entry point to the workflow context
- **AND** the owner SHALL be able to reach the workflow context from the entry without losing the Activity orientation

#### Scenario: Runner event links to runner detail

- **WHEN** a runner event is shown on the Activity page
- **THEN** the entry SHALL link to runner detail
- **AND** the link SHALL be project-scoped

### Requirement: Generic agent-launched sessions are surfaced alongside workflow-bound sessions

The Activity page SHALL surface generic agent-launched sessions (sessions not bound to a workflow run) alongside workflow-bound sessions, so the full execution picture is visible in one place. Generic sessions SHALL appear with the same event-type identity and navigation entry points as workflow-bound sessions, and SHALL NOT be hidden from the Activity view solely because they lack a workflow binding.

#### Scenario: Generic agent session appears on Activity

- **WHEN** a generic agent-launched session exists (no workflow binding)
- **THEN** the session SHALL appear on the Activity page
- **AND** it SHALL carry an event-type identity comparable to workflow-bound sessions

#### Scenario: Generic and workflow-bound sessions are visible together

- **WHEN** both generic agent sessions and workflow-bound sessions exist
- **THEN** both SHALL be visible on the same Activity page
- **AND** the owner SHALL NOT need to navigate to a separate agent view to see generic sessions

### Requirement: Activity status and surfaces use the shared theme-token language in light and dark mode

All status and surface presentation on the Activity page SHALL route through the shared theme-token families (success / warning / info / danger, including their -subtle / -border / -foreground variants) so that light and dark mode treatment matches the shared status and surface language. Ad-hoc hardcoded Tailwind palette classes for event categories and status (e.g. `bg-red-500`, `bg-red-50`, `text-red-700`) SHALL be replaced by the shared theme-token families. The Activity page SHALL NOT introduce a parallel, hardcoded status palette that diverges from the shared language.

#### Scenario: Failure events use the shared danger tokens in both themes

- **WHEN** a failure event is rendered on the Activity page in light mode and in dark mode
- **THEN** its marker, accent, and surface SHALL use the shared danger theme-token family
- **AND** it SHALL NOT use a hardcoded `bg-red-*` / `text-red-*` palette class

#### Scenario: Approval and blocked events use the shared warning tokens

- **WHEN** an approval or blocked event is rendered on the Activity page
- **THEN** its visual priority SHALL use the shared warning theme-token family
- **AND** it SHALL NOT use a hardcoded amber/orange palette class diverging from the shared language

#### Scenario: Routine events use the shared neutral surface tokens

- **WHEN** a routine low-priority event is rendered on the Activity page
- **THEN** its surface SHALL use the shared neutral/background theme tokens
- **AND** it SHALL NOT use a hardcoded `bg-gray-*` palette class that diverges from the shared surface language

### Requirement: Activity evidence is consumed from recorded events without changing event recording

The Activity evidence view SHALL consume recorded events as its input. It SHALL NOT change how events or session transcripts are recorded, and SHALL NOT add new event subscription behavior. A project-scoped event read endpoint that queries already-recorded events is permitted; it does not change event recording or add subscription behavior. Event-type identity and attention level SHALL be derived from the recorded event data, not from new internal implementation fields exposed as product concepts.

#### Scenario: No new event recording or subscription is introduced

- **WHEN** the Activity evidence view is implemented
- **THEN** it SHALL consume recorded events via a project-scoped read endpoint
- **AND** no new event-recording, event-emission, or event-subscription behavior SHALL be added

#### Scenario: Hidden internal fields are not exposed as product concepts

- **WHEN** an Activity entry is rendered
- **THEN** its event-type label and attention level SHALL be expressed in production/domain terms
- **AND** raw internal implementation field names SHALL NOT be surfaced as product-facing labels
