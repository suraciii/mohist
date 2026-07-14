### Requirement: Coder Session has a stable, scannable layout with defined evidence regions

The Coder Session page SHALL be organized into a stable, scannable layout where each class of execution evidence has a defined place and does not compete in a single undifferentiated scroll. The layout SHALL define separate, identifiable regions for: task identity, current status, the turn transcript, tool calls, errors, token/context usage, and sibling sessions. Each region SHALL be reachable as a recognizable part of the page rather than interleaved into one long stream, so the owner can scan what happened and whether action is needed without re-reading the whole transcript.

#### Scenario: Task identity has a defined region

- **WHEN** the owner opens a Coder Session page
- **THEN** a task identity region SHALL be present showing the session/task identity
- **AND** it SHALL be distinguishable from the transcript, usage, and sibling regions

#### Scenario: Current status has a defined region

- **WHEN** the owner opens a Coder Session page
- **THEN** a current status region SHALL be present showing the session's current status
- **AND** it SHALL occupy a defined place rather than being embedded inline in the transcript

#### Scenario: Turns and tool calls are readable as evidence, not a single undifferentiated scroll

- **WHEN** the session has turns and tool calls
- **THEN** the turn transcript region SHALL present turns and tool calls in a readable hierarchy
- **AND** tool calls SHALL be distinguishable from plain message turns so the owner can scan agent actions

#### Scenario: Errors are surfaced as their own evidence, not buried in the transcript

- **WHEN** the session has errors (session failure, failure category, or tool errors)
- **THEN** an errors evidence region SHALL surface the error information
- **AND** the owner SHALL NOT be required to scroll the full transcript to discover that a failure occurred

#### Scenario: Token and context usage have a defined region

- **WHEN** the session has token or context-usage data
- **THEN** a token/context usage region SHALL be present in a defined place
- **AND** it SHALL NOT be scattered across the header and transcript without a recognizable home

#### Scenario: Sibling sessions occupy a stable reference region

- **WHEN** the session has sibling sessions
- **THEN** a sibling sessions region SHALL occupy a stable reference place in the layout
- **AND** it SHALL NOT float or relocate depending on transcript length

#### Scenario: Sibling region remains stable when the session has no siblings

- **WHEN** the session has no sibling sessions (e.g. a generic agent session)
- **THEN** the layout SHALL degrade gracefully without a sibling region
- **AND** the remaining regions SHALL retain their defined places

### Requirement: Density and grouping make execution evidence readable

The Coder Session layout SHALL use density and grouping so that execution evidence is readable at a scan. Task identity, status, usage, and errors SHALL be compact summary regions that fit without forcing the owner to scroll past them to reach the transcript, while the transcript region SHALL carry the detailed turn and tool-call evidence. The layout SHALL NOT pack task identity, status, turns, tool calls, errors, token/context usage, and sibling sessions into a single undifferentiated scroll where no class of evidence has a recognizable boundary.

#### Scenario: Summary regions stay compact above the transcript

- **WHEN** the owner opens a session with usage, status, and error data
- **THEN** the identity, status, usage, and error summary regions SHALL be compact
- **AND** the owner SHALL be able to reach the transcript region without scrolling past an unbounded summary

#### Scenario: Transcript detail does not displace the summary regions

- **WHEN** the transcript grows long
- **THEN** the task identity, status, usage, and error summary regions SHALL retain their defined places
- **AND** the transcript SHALL NOT push the summaries off-screen or merge them into the scroll

#### Scenario: Sticky identity and status remain visible while scanning the transcript

- **WHEN** the owner scrolls within the transcript region
- **THEN** a sticky identity/status affordance SHALL keep the session identity and current status visible
- **AND** the owner SHALL NOT lose orientation about which session and which status is being viewed

### Requirement: Coder Session provides orientation-preserving navigation entry points

From the Coder Session page, the owner SHALL be able to navigate to the relevant issue, the workflow context, and sibling or lineage evidence without losing orientation. Navigation entry points SHALL preserve orientation by linking to project-scoped destinations and by providing a back path that returns to the originating context (issue, agent, or activity). A generic agent session that has no issue binding SHALL provide an entry point to its agent context in place of an issue link.

#### Scenario: Session links back to the relevant issue

- **WHEN** a session is bound to an issue
- **THEN** the page SHALL provide a back/entry link to that issue
- **AND** the link SHALL be project-scoped

#### Scenario: Session provides an entry point to workflow context

- **WHEN** a session belongs to a workflow run
- **THEN** the page SHALL provide an entry point to the workflow context
- **AND** the owner SHALL be able to return from that context to the session without losing orientation

#### Scenario: Sibling sessions link to their evidence

- **WHEN** sibling sessions are listed in the sibling region
- **THEN** each sibling SHALL provide a link to that sibling session
- **AND** navigating to a sibling SHALL preserve the project-scoped session orientation

#### Scenario: Lineage evidence is reachable when compaction lineage exists

- **WHEN** a session has compaction lineage (runtime session lineage of length two or more)
- **THEN** the page SHALL provide an entry point to the lineage evidence
- **AND** the owner SHALL be able to follow the lineage without losing the current session orientation

#### Scenario: Generic session without an issue links to its agent context

- **WHEN** a generic agent session has no issue binding
- **THEN** the page SHALL provide a back/entry link to its agent context
- **AND** it SHALL NOT fabricate an issue link where none exists

### Requirement: Coder Session status and surfaces use the shared theme-token language in light and dark mode

All status and surface presentation on the Coder Session page SHALL route through the shared theme-token families (success / warning / info / danger, including their -subtle / -border / -foreground variants) so that light and dark mode treatment matches the shared status and surface language. Ad-hoc hardcoded Tailwind palette classes for status badges, failure accents, and recovery surfaces (e.g. `bg-red-100 text-red-700`, `text-red-500`, `bg-yellow-100 text-yellow-700`) SHALL be replaced by the shared theme-token families. The page SHALL NOT introduce a parallel, hardcoded status palette that diverges from the shared language.

#### Scenario: Status badge uses shared tokens across all status kinds

- **WHEN** the session status badge is rendered for running, completed, failed, stale, and finalizing states in light and dark mode
- **THEN** each status kind SHALL use the shared theme-token family appropriate to its meaning (info for running, success for completed, danger for failed, warning for stale/finalizing)
- **AND** it SHALL NOT use hardcoded `bg-*-100 text-*-700` palette classes that diverge from the shared language

#### Scenario: Failure and error accents use the shared danger tokens

- **WHEN** a session failure reason, failure category, or tool-error accent is rendered
- **THEN** the accent SHALL use the shared danger theme-token family
- **AND** it SHALL NOT use a hardcoded `text-red-*` or `bg-red-*` palette class

#### Scenario: Recovery and context-health surfaces use shared tokens

- **WHEN** the recovery bar, context-health bar, or usage summary is rendered
- **THEN** its surface and accents SHALL use the shared theme-token families
- **AND** it SHALL NOT use a hardcoded palette class diverging from the shared surface language

### Requirement: Existing session evidence and actions are preserved

This change SHALL preserve the existing Coder Session evidence and actions: the turn transcript and tool-call rendering, the follow-up composer, the cancel control and its confirmation, the compact/reset/recovery actions, the context-health and usage summaries, and the sibling/lineage navigation. No session evidence, lifecycle action, or recovery action SHALL be removed or respecified. Repositioning a region within the new layout SHALL NOT change the action's gating semantics or the data it consumes.

#### Scenario: Transcript, follow-up, and cancel remain available

- **WHEN** the owner views a running session in the new layout
- **THEN** the turn transcript, the follow-up composer, and the cancel control with its confirmation SHALL remain available
- **AND** their gating (enabled while running, confirmation before cancel) SHALL match the pre-existing behavior

#### Scenario: Recovery and compact/reset actions remain available in their valid contexts

- **WHEN** a session has recovery actions or compact/reset eligibility
- **THEN** those actions SHALL remain available within the new layout
- **AND** their enabling conditions SHALL match the pre-existing projections

#### Scenario: Existing data-testid anchors are preserved where still valid

- **WHEN** the new layout is rendered
- **THEN** existing data-testid anchors that identify still-valid regions (transcript scroll container, sticky title, recovery bar, sibling navigation slot, cancel triggers) SHALL be preserved
- **AND** new regions SHALL be identified by anchors that do not collide with the existing ones

### Requirement: Coder Session evidence is consumed from existing projections without changing transcript recording

The Coder Session evidence view SHALL consume existing session, transcript, usage, and sibling projections. It SHALL NOT change how session transcripts are recorded, and SHALL NOT add new session subscription behavior. The layout reorganization SHALL NOT introduce new API, DTO, or query changes.

#### Scenario: No new transcript recording or subscription is introduced

- **WHEN** the Coder Session evidence view is implemented
- **THEN** it SHALL consume the existing session, transcript, usage, and sibling data sources
- **AND** no new transcript-recording, session-emission, or session-subscription behavior SHALL be added

#### Scenario: Hidden internal fields are not exposed as product concepts

- **WHEN** a Coder Session region is rendered
- **THEN** its labels SHALL be expressed in production/domain terms (task identity, status, turns, tool calls, errors, usage, siblings)
- **AND** raw internal implementation field names SHALL NOT be surfaced as product-facing labels
