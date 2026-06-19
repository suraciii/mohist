## REMOVED Requirements

### Requirement: Timeline loads merged event history on issue open

**Reason:** The Activity timeline no longer renders on Issue Detail initial load. It now lives behind an on-demand dialog, so the load trigger is owned by the `issue-detail-activity-dialog` capability (lazy-load on dialog open).

**Migration:** See `issue-detail-activity-dialog` — "Activity event history is lazy-loaded when the dialog opens". The merged-history and empty-state semantics are preserved there and in the modified `web-ui` events-fetch requirement.

### Requirement: Timeline applies category color coding

**Reason:** The six-category saturated color scheme (colored dots plus category tags for workflow/approval/integration/success/failures/metadata) is being removed for regular events. Regular categories now render in neutral monochrome; only failure and attention-required events retain colored emphasis. The new color policy is captured in the modified "Timeline visually emphasizes failures and attention-required events" requirement.

**Migration:** Renderers that assigned a saturated color and a category tag to every category SHALL instead apply neutral monochrome to workflow, approval, integration, success, and metadata events. Colored emphasis is reserved for failure and attention-required events only, per the modified emphasis requirement.

## MODIFIED Requirements

### Requirement: Timeline updates in real time while workflow is active

While the Activity surface is open and the issue's workflow is active, the event timeline SHALL append newly arrived events in real time over the existing SignalR bus without a full page reload. The live enter animation SHALL be converged or removed so newly appended events do not dominate the surface with motion. The timeline SHALL NOT be required to accumulate live events while the Activity surface is closed; events that arrive while it is closed are recovered by re-fetching the persisted history on the next open.

#### Scenario: New event arrives while the Activity surface is open

- **WHEN** the Activity surface is open for an issue with an active workflow run
- **AND** a new workflow or issue event arrives over SignalR for that issue
- **THEN** the event timeline SHALL append the new event without reloading
- **AND** the appended event SHALL NOT use a loud full-row motion entrance that competes with the neutral reading experience

#### Scenario: Timeline stops accumulating when issue is closed

- **WHEN** the issue's workflow is no longer active
- **THEN** the timeline SHALL continue to display all loaded events
- **AND** the Live indicator SHALL stop pulsing to reflect that no live updates are expected

#### Scenario: Closed surface does not need to accumulate live events

- **WHEN** the Activity surface is closed and new events arrive
- **THEN** the timeline SHALL NOT be required to mount or accumulate those events live
- **AND** the events SHALL be recovered by re-fetching the persisted history the next time the surface is opened

### Requirement: Timeline visually emphasizes failures and attention-required events

The timeline SHALL render regular event categories (workflow/lifecycle, approval, integration, success, and metadata) in neutral monochrome. It SHALL NOT apply category-saturated colors, category badges/tags, or full-row colored backgrounds to those regular categories. The timeline SHALL visually distinguish failures and attention-required events (stage failed, run failed, approval requested, rebase conflict, base drift needs-attention) from regular events using a colored accent on the event marker (for example a colored dot or haloed dot) so the eye finds decisions and failures fast. Failure and attention-required events SHALL NOT use a full-row tinted background; their emphasis SHALL come from the marker accent rather than a full-row color fill.

#### Scenario: Regular events use neutral monochrome

- **WHEN** the timeline renders a workflow-lifecycle, approval, integration, success, or metadata event
- **THEN** the row SHALL use a neutral monochrome treatment
- **AND** the row SHALL NOT render a category-saturated color, a category badge/tag, or a full-row colored background

#### Scenario: Failed stage is visually emphasized via marker accent only

- **WHEN** the timeline renders a stage-failed event
- **THEN** the event marker SHALL use a colored accent distinct from regular neutral events
- **AND** the row SHALL NOT use a full-row tinted background

#### Scenario: Approval requested is visually emphasized via marker accent only

- **WHEN** the timeline renders an approval-requested event
- **THEN** the event marker SHALL use a colored accent distinct from regular neutral events
- **AND** the row SHALL NOT use a full-row tinted background

#### Scenario: Normal progress is not colored

- **WHEN** the timeline renders a task-started or run-resumed event
- **THEN** the row SHALL NOT use a category-saturated color, a category badge/tag, or a colored marker accent

### Requirement: Timeline failures expand inline detail

Failure events SHALL expose an inline detail block that the user can expand to see the surrounding context, such as conflicting file paths, error messages, or failing step output. The expanded detail block SHALL use a neutral light background (paper/ink) instead of a dark background such as `bg-gray-900`, consistent with the neutral visual treatment of the timeline.

#### Scenario: Rebase conflict expands file paths

- **WHEN** a rebase-conflict event row is expanded
- **THEN** the inline detail block SHALL display the conflicting file paths in a monospaced font
- **AND** the detail block SHALL use a neutral light background rather than a dark background

#### Scenario: Stage failure expands error detail

- **WHEN** a stage-failed event row is expanded
- **THEN** the inline detail block SHALL display the failure reason or error message in a monospaced font
- **AND** the detail block SHALL use a neutral light background rather than a dark background
