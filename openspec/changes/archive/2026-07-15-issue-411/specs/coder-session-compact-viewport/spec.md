### Requirement: Transcript region preserves a usable reading surface at compact viewports

The Coder Session reading layout SHALL preserve a readable, scrollable transcript region at compact mobile viewports (viewports below the `md` breakpoint, where the fixed mobile bottom navigation is visible). The transcript scroll container SHALL NOT collapse to zero or near-zero height as a result of static summary regions and the follow-up composer consuming the available viewport height. The layout SHALL guarantee the transcript region a minimum usable height at compact viewports so the owner can read and scroll execution evidence on initial load rather than seeing a thin strip or no visible transcript area.

#### Scenario: Transcript remains readable at 375x667

- **WHEN** the owner opens a Coder Session page with turns at a 375x667 viewport
- **THEN** the transcript scroll container SHALL present a readable, scrollable reading surface on initial load
- **AND** it SHALL NOT be reduced to a thin strip hidden by mobile navigation

#### Scenario: Transcript never collapses to zero at 320x568

- **WHEN** the owner opens a Coder Session page with turns at a 320x568 viewport
- **THEN** the transcript scroll container SHALL retain a non-zero height
- **AND** the owner SHALL be able to read and scroll transcript content

#### Scenario: Transcript scroll is independent of summary and composer at compact viewports

- **WHEN** the transcript content exceeds the visible transcript region at a compact viewport
- **THEN** the transcript scroll container SHALL scroll independently
- **AND** scrolling the transcript SHALL NOT displace the session header, usage summary, or follow-up composer from their layout positions

### Requirement: Compact layout reduces nonessential summary density before the transcript reading surface

At compact mobile viewports, the Coder Session layout SHALL reduce nonessential session-summary density before reducing the transcript reading surface. Session identity (the session name) and current status SHALL remain visible at all compact viewports. Nonessential summary metadata - including model, turn count, last-activity time, changed-files summary, duration, and session identifier - SHALL be reduced in density at compact viewports so that the summary regions do not consume the vertical space required for the transcript. The usage summary region SHALL be presented in a compact form at compact viewports.

#### Scenario: Session identity and status remain visible at compact viewports

- **WHEN** the owner opens a Coder Session page at a compact viewport
- **THEN** the session name SHALL remain visible
- **AND** the current status SHALL remain visible
- **AND** neither SHALL be removed to make room for transcript content

#### Scenario: Nonessential summary metadata is reduced at compact viewports

- **WHEN** the owner opens a Coder Session page at a compact viewport with full summary metadata (model, turn count, last-activity, changed files, duration, session id)
- **THEN** the summary region SHALL occupy less vertical space than at desktop viewports
- **AND** the density reduction SHALL occur before the transcript reading surface is reduced

#### Scenario: Usage summary is compact at compact viewports

- **WHEN** the session has token or context-usage data and the owner views the page at a compact viewport
- **THEN** the usage summary region SHALL be presented in a compact form
- **AND** it SHALL NOT expand to consume the space reserved for the transcript reading surface

### Requirement: Session controls are reachable at compact viewports

The existing session controls - the Compact and Reset recovery actions, the follow-up composer, and the cancel control - SHALL be reachable through normal page navigation at compact mobile viewports. No session control SHALL be positioned below the reachable viewport or hidden by fixed mobile navigation such that the owner cannot reach it without resizing the window or dismissing other content.

#### Scenario: Compact and Reset recovery controls are reachable at compact viewports

- **WHEN** a session has recovery actions and the owner views the page at a compact viewport
- **THEN** the Compact and Reset controls SHALL be reachable through normal page navigation
- **AND** they SHALL NOT be positioned below the reachable viewport or covered by fixed mobile navigation

#### Scenario: Follow-up composer is reachable at compact viewports

- **WHEN** a running session is viewed at a compact viewport
- **THEN** the follow-up composer SHALL be reachable through normal page navigation
- **AND** it SHALL NOT be covered by fixed mobile navigation

#### Scenario: Cancel control is reachable at compact viewports

- **WHEN** a running session with a cancel control is viewed at a compact viewport
- **THEN** the cancel control SHALL be reachable through normal page navigation
- **AND** it SHALL NOT be covered by fixed mobile navigation

### Requirement: Fixed mobile navigation does not occlude transcript content or session controls

The Coder Session layout SHALL reserve space for the fixed mobile bottom navigation at compact viewports so that the navigation does not cover transcript content or session controls. No transcript content or session control SHALL be occluded by the fixed mobile navigation at any tested compact viewport (375x667, 320x568).

#### Scenario: Transcript content is not covered by mobile navigation

- **WHEN** the owner views a Coder Session page at a compact viewport
- **THEN** the fixed mobile bottom navigation SHALL NOT overlap the transcript scroll container's visible content area
- **AND** the layout SHALL reserve bottom space for the navigation

#### Scenario: Session controls are not covered by mobile navigation

- **WHEN** session controls are present at a compact viewport
- **THEN** the fixed mobile bottom navigation SHALL NOT overlap any session control
- **AND** the owner SHALL be able to interact with every visible control without the navigation intercepting the interaction

### Requirement: Desktop and larger mobile layouts retain existing behavior

The compact-viewport accommodations SHALL apply only below the `md` breakpoint. At the `md` breakpoint and above, the Coder Session page SHALL retain its existing session evidence regions, navigation, and control behavior without compact-viewport modifications. The desktop layout, region order, summary density, transcript behavior, and control placement SHALL match the pre-existing evidence-view layout.

#### Scenario: Desktop layout is unchanged at md and above

- **WHEN** the owner views a Coder Session page at a viewport at or above the `md` breakpoint
- **THEN** the session header, usage summary, errors evidence, transcript, follow-up composer, and sibling sidebar SHALL retain their existing layout, density, and behavior
- **AND** no compact-viewport accommodation SHALL be applied

#### Scenario: Compact accommodations do not persist above the md breakpoint

- **WHEN** the viewport transitions from compact to `md` or above
- **THEN** any compact-viewport density reduction, height constraint, or control repositioning SHALL be reverted
- **AND** the desktop evidence-view layout SHALL be restored

### Requirement: Existing evidence regions, anchors, and action gating are preserved

The compact-viewport changes SHALL NOT remove existing Coder Session evidence regions, break existing region anchors, or change recovery gating, session lifecycle, status semantics, or transcript recording. The region contract established by the evidence-view layout SHALL remain intact at all viewports; only the density and reachability of regions at compact viewports SHALL change. No new session control or recovery operation SHALL be introduced.

#### Scenario: Existing region anchors are preserved

- **WHEN** the compact-viewport layout is rendered
- **THEN** existing region anchors that identify still-valid regions (session header, transcript scroll container, sticky title, recovery bar, follow-up composer, sibling navigation) SHALL be preserved
- **AND** no anchor SHALL be removed or renamed

#### Scenario: Recovery and session lifecycle gating is unchanged

- **WHEN** the compact-viewport layout is rendered for a session with recovery actions or lifecycle controls
- **THEN** the enabling conditions and gating for Compact/Reset, cancel, and follow-up SHALL match the pre-existing behavior
- **AND** no new session control or recovery operation SHALL be introduced

#### Scenario: Transcript recording and status semantics are unchanged

- **WHEN** the compact-viewport layout is implemented
- **THEN** it SHALL consume existing session, transcript, usage, and sibling data sources without mutation
- **AND** no new transcript-recording, session-emission, or status-semantics behavior SHALL be introduced
