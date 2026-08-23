### Requirement: The timeline projection is web's only session-event projection

`entities/session` SHALL expose exactly one presentation projection for session activity: the timeline projection under `model/timeline` (domain-action detection, item derivation, item grouping, and the `Timeline*` types). The SessionEvent view projections — `viewSessionEvents` and the chat, compact, and timeline view builders under `model/view` — the `SessionEvent` and `SessionView*` type family, and their re-exports (including `@x/session-view.ts`) MUST NOT exist in web after this change.

#### Scenario: Projecting session activity for presentation

- **WHEN** web code reduces session turns or session events into a presentable structure
- **THEN** it SHALL use the timeline projection that produces timeline facts, items, and grouped entries
- **AND** it MUST NOT build chat, compact, or timeline views from a raw session-event list

#### Scenario: The session entity exports only the timeline projection

- **WHEN** another slice imports from `entities/session`'s public API
- **THEN** the available exports SHALL be the timeline functions and types (`detectShellDomainAction`, `detectToolDomainAction`, `deriveTimelineItems`, `groupTimelineItems`, `isTimelineGroup`, and the `Timeline*` types)
- **AND** `viewSessionEvents`, `SessionEvent`, and the `SessionView*` / `SessionChat*` / `SessionCompactView` / `SessionTimelineRound` view type family SHALL be absent

#### Scenario: No module depends on the removed view family

- **WHEN** the web application type-checks after this change
- **THEN** no module, including `entities/coder-session` model types, SHALL import `SessionEvent` or any removed view type

### Requirement: The session-transcript chain is the only session presentation chain

`widgets/session-transcript`, fed by the timeline projection, SHALL be the only production component chain that presents a session's activity. The dead `widgets/coder-session` presentation chain MUST NOT exist: the SessionCard family (`ActiveSessionCard`, `RecentCard`, `WaitingCard`), `SessionTimeline`, `PlanProgressPanel`, the compaction views (`CompactionTimelineEntry`, `CompactionCompactSummary`), the timeline model (`model/anomaly.tsx`, `model/useSessionTimeline.ts`, `model/session-timeline-reducer.ts`), and their public exports. Still-consumed `widgets/coder-session` exports SHALL remain available: activity events (`useActivityEvents`, `buildActivityEvents`, `sortActivityEvents` and their types), the usage snapshot (`useActivityUsageSnapshot`), `SessionFollowupComposer`, `SessionRecoveryActions`, `ContextHealthBar`, and `UsageSnapshotLabel`.

#### Scenario: Presenting a session timeline

- **WHEN** the session detail page renders a session's activity
- **THEN** it SHALL render through the session-transcript widget chain fed by the timeline projection
- **AND** no component of the deleted coder-session presentation chain SHALL be reachable

#### Scenario: Surviving widget exports stay available

- **WHEN** the activity page or the session detail shell imports session presentation widgets from `widgets/coder-session`
- **THEN** the still-consumed exports (activity events, usage snapshot, followup composer, recovery actions, context health bar, usage snapshot label) SHALL resolve from the slice's public API
- **AND** the public API SHALL NOT export the deleted chain's components or types

### Requirement: Session activity reduces to timeline facts

The session-transcript timeline model SHALL reduce session transcript turns, live transcript details, and session-summary observations into an ordered list of `TimelineFact`s. Each fact SHALL carry a stable source identity (`sourceId`), a fact source (`transcript`, `live`, `input`, `turn`, `summary`, `recovery`, or `system`), ordering (`order`, `occurredAt`), and a fact kind (`input`, `message`, `reasoning`, `tool`, `plan`, `status`, `boundary`, `error`, or `suppressed`), with tool facts carrying the tool descriptor including call id, name, input, output, status, and changed files.

#### Scenario: Facts derive from transcript and live activity

- **WHEN** a session has transcript turns plus live details from a running turn
- **THEN** both SHALL reduce to facts with distinct stable source ids and kinds
- **AND** facts describing the same tool call SHALL share its call id so later derivation merges them

### Requirement: Facts derive into renderable timeline items

`deriveTimelineItems` SHALL sort facts by order and source id and classify each into a `TimelineItem` carrying an id, source ids, occurrence time, a render class (`input`, `message`, `reasoning`, `file-read`, `file-edit`, `shell`, `domain-action`, `plan`, `tool`, `status`, `boundary`, `error`, or `suppressed`), a human-readable summary, a salience, an optional detail, and terminal or streaming state. Tool facts SHALL classify by normalized tool name into file-read, file-edit, shell, plan, or generic tool classes; a tool whose command or call matches a recognized Mohist domain action SHALL render as a domain-action item carrying a reference to the affected entity; a failed, cancelled, timed-out, or non-zero-exit tool SHALL render as an error item with critical salience and terminal state. Facts for the same tool call SHALL merge into one item, and streamed message or reasoning facts sharing a correlation id SHALL merge into one streaming item whose summary accumulates the streamed text until a later non-streaming fact finalizes it.

#### Scenario: Classifying a file edit tool

- **WHEN** a completed tool fact names a file-edit tool with a target path and changed-file counts
- **THEN** the derived item SHALL have render class file-edit, a summary naming the path with the addition and deletion counts, and terminal state

#### Scenario: A failing tool becomes a critical error item

- **WHEN** a tool fact reports failed, cancelled, or timeout status or a non-zero exit code
- **THEN** the derived item SHALL have render class error, critical salience, and terminal state
- **AND** its detail SHALL expose the tool input, output, changed files, and error message

#### Scenario: A Mohist CLI command renders as a domain action

- **WHEN** a shell or tool fact invokes a recognized Mohist domain action such as commenting on an issue or approving a run
- **THEN** the derived item SHALL have render class domain-action with a summary of the action's verb and object and a reference to the affected entity

#### Scenario: Live and transcript facts for one tool call merge

- **WHEN** a live tool fact is followed by the transcript fact for the same tool call
- **THEN** derivation SHALL produce a single item whose detail and terminal state reflect the transcript fact

#### Scenario: Streamed reasoning accumulates and finalizes

- **WHEN** streamed reasoning facts share a correlation id and a later non-streaming fact arrives
- **THEN** the streamed facts SHALL merge into one streaming item with an accumulated summary
- **AND** the later fact SHALL finalize that item as terminal and non-streaming

### Requirement: Terminal tool runs collapse into groups

`groupTimelineItems` SHALL walk derived items in order and collapse a run of three or more compatible, terminal items of the file-read, shell, or tool render classes into one `TimelineGroup` entry identified from its first and last item ids, carrying the member items, a count-based summary, low salience, and the flattened source ids. Runs shorter than three, items of other render classes, non-terminal items, failed items, and items with conflicting group keys SHALL remain individual entries in their original order.

#### Scenario: Collapsing consecutive file reads

- **WHEN** four terminal file-read items appear consecutively with compatible grouping
- **THEN** they SHALL be replaced by one group entry whose summary reports the member count and whose items carry the originals

#### Scenario: Preserving short runs and failures

- **WHEN** only two compatible terminal shell items are consecutive, or a run contains a failed item
- **THEN** those items SHALL remain as individual entries in their original order

### Requirement: The transcript timeline exposes current activity

`useSessionTimeline` in `widgets/session-transcript` SHALL compose facts, derived items, and grouped entries into one result (`facts`, `items`, `entries`, `currentActivity`) for the session transcript layout. `currentActivity` SHALL be derived as `queued`, `active`, `idle`, or `unknown` with a display label and the source or item id that produced it: a queued current turn yields queued; observed idle or unknown session activity yields the matching state; otherwise the latest non-terminal item yields active with that item's summary, falling back to active from the turn or session activity state, and finally unknown.

#### Scenario: Deriving current activity from a queued turn

- **WHEN** the session summary reports a current turn whose state is queued
- **THEN** current activity SHALL be queued with a display label and the turn-state source id

#### Scenario: Deriving current activity from a running item

- **WHEN** the session is active and the latest non-terminal item is an executing tool
- **THEN** current activity SHALL be active with that item's summary and item id
