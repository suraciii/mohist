### Requirement: Attention-first zone priority

The dashboard MUST organize its content into zones in a fixed priority order: needs-attention first, then active production, then capacity, then recent history. When a higher-priority zone has content, lower-priority zones MUST yield visual prominence so the first screen is led by what needs the owner's attention rather than by routine or historical information.

#### Scenario: needs-attention leads the first screen

- **WHEN** one or more needs-attention states exist
- **THEN** the needs-attention zone MUST appear before the active-production, capacity, and recent-history zones on the first screen

#### Scenario: lower-priority zones yield while needs-attention content exists

- **WHEN** the needs-attention zone has content
- **THEN** the active-production, capacity, and recent-history zones MUST NOT occupy equal or greater prominence than the needs-attention zone on the first screen

#### Scenario: capacity is a distinct level from recent history

- **WHEN** the dashboard renders its priority order
- **THEN** runner capacity usage MUST appear as its own level, ordered before recent history and separate from it

### Requirement: Needs-attention states surfaced without leaving the dashboard

The dashboard MUST surface, in its needs-attention zone, every state that requires owner action: approval gates (issues awaiting approval), blocked issues, interrupted issues, integration-failed issues, runner-unavailable, and runner capacity-limited. The owner MUST be able to see all of these states on the first screen without opening the issue board or the runner board.

#### Scenario: runner capacity-limited surfaces as a needs-attention state

- **WHEN** the runner is available but its active slots are at or above the configured maximum (capacity.active >= capacity.max)
- **THEN** the dashboard MUST surface a runner capacity-limited needs-attention signal

#### Scenario: runner capacity-limited does not fire below the maximum

- **WHEN** the runner is available and its active slots are below the configured maximum (capacity.active < capacity.max)
- **THEN** the dashboard MUST NOT surface a runner capacity-limited needs-attention signal

#### Scenario: runner-unavailable surfaces as a needs-attention state

- **WHEN** the runner is unavailable (runnerAvailable is false)
- **THEN** the dashboard MUST surface a runner-unavailable needs-attention signal, and MUST surface it even when no issue-derived attention items exist

#### Scenario: approval gate surfaces as a needs-attention state

- **WHEN** an issue is awaiting approval
- **THEN** the dashboard MUST surface an approval-gate needs-attention signal referencing that issue

#### Scenario: blocked, interrupted, and integration-failed surface as needs-attention states

- **WHEN** an issue is blocked, interrupted, or failed at the integrate stage
- **THEN** the dashboard MUST surface the corresponding needs-attention signal referencing that issue

### Requirement: Running issues show workflow stage and owner-action cue

The active-production zone MUST show running (in-progress) issues — not only issues that currently have an active agent session — and each MUST display its current workflow stage and a cue indicating whether owner action is needed. Work that is in progress but paused between workflow stages MUST remain visible on the first screen.

#### Scenario: in-progress issue with no active session stays visible

- **WHEN** an issue is in progress but has no active agent session (for example, it is paused between workflow stages)
- **THEN** the active-production zone MUST still show that issue with its current workflow stage

#### Scenario: running issue shows its current workflow stage

- **WHEN** an in-progress issue is shown in the active-production zone
- **THEN** the zone MUST display that issue's current workflow stage

#### Scenario: running issue distinguishes owner-action-needed from normally-running

- **WHEN** a running issue requires owner action
- **THEN** the active-production zone MUST surface a cue that distinguishes it from an issue that is running normally and needs no action

### Requirement: Empty zones collapse when higher-priority content exists

When there are active, blocked, interrupted, or approval-waiting issues, empty or low-value dashboard zones MUST collapse rather than occupy reserved fixed-height boxes, so empty areas do not dominate the first screen.

#### Scenario: empty zone collapses while competing content exists

- **WHEN** a dashboard zone has no content AND there is at least one needs-attention state or active/running issue
- **THEN** that empty zone MUST collapse instead of occupying a reserved fixed-height box

#### Scenario: empty zones stop dominating the first screen

- **WHEN** there are active, blocked, interrupted, or approval-waiting issues
- **THEN** empty dashboard sections MUST NOT dominate the most prominent area of the first screen

### Requirement: Concise ready state when nothing needs attention or is active

When nothing needs attention and nothing is active, the dashboard MUST present a concise ready state instead of a large empty layout dominated by empty fixed-height zones.

#### Scenario: concise ready state when idle

- **WHEN** there are no needs-attention states and no active/running issues
- **THEN** the dashboard MUST show a concise ready state rather than a large empty layout

### Requirement: Headline remains subordinate to the attention zone

The factory status headline MUST remain a compact status strip atop the overview and MUST stay subordinate to the needs-attention zone; it MUST NOT become the most prominent element on the first screen when needs-attention content exists.

#### Scenario: headline stays a compact strip above the attention zone

- **WHEN** the dashboard renders
- **THEN** the factory status headline MUST appear as a compact status strip positioned above the needs-attention zone and MUST NOT be more prominent than the needs-attention zone
