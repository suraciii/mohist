### Requirement: Summary area precedes the full description

The Epic detail page SHALL render a summary area before the full epic description on both desktop and mobile viewport widths. The summary area SHALL surface, at minimum, progress (delivered vs total linked issues), current activity (active and blocked linked issues), and the next issue or next-issue reason, plus the ready-to-mark-done state when applicable. The full epic description SHALL be demoted to a secondary Overview/Description region rendered after the summary area. A user SHALL be able to see progress, current activity, and the next issue / next-issue reason within the first fold without first scrolling past the full description.

#### Scenario: Summary renders before the description on desktop
- **WHEN** the Epic detail page is rendered for a non-empty epic description on a desktop viewport
- **THEN** the summary area SHALL appear in the DOM and in visual order before the Overview/Description region
- **AND** the summary area SHALL display progress, current activity, and the next issue or next-issue reason

#### Scenario: Summary is reachable in the first fold on mobile
- **WHEN** the Epic detail page is rendered for an epic with a long description at a mobile viewport width (e.g. 390px)
- **THEN** the summary area (progress, current activity, next issue / reason) SHALL be visible without scrolling past the full description
- **AND** the full description SHALL NOT be required reading before the summary is reached

#### Scenario: Epic without a description still renders the summary
- **WHEN** the Epic detail page is rendered for an epic whose description is empty
- **THEN** the summary area SHALL still render with progress, current activity, and next issue / reason
- **AND** no empty Overview/Description region SHALL be rendered

### Requirement: Progress summary content

The summary area SHALL display the delivered linked-issue count against the total linked-issue count, and SHALL reflect the ready-to-mark-done state. When every linked issue is delivered, the summary SHALL surface a ready-to-mark-done indication alongside the counts.

#### Scenario: Partial progress shows delivered over total
- **WHEN** the summary is rendered for an epic with delivered count 2 and total count 5
- **THEN** the summary SHALL display the delivered and total counts (e.g. `2 / 5`)
- **AND** SHALL NOT display a ready-to-mark-done indication

#### Scenario: All delivered shows ready-to-mark-done
- **WHEN** the summary is rendered for an epic whose linked issues are all delivered (`readyToMarkDone` is true)
- **THEN** the summary SHALL surface a ready-to-mark-done indication
- **AND** SHALL still display the delivered and total counts

### Requirement: Current activity summary with issue navigation

The summary area SHALL list the currently active and blocked linked issues (those currently in-progress or health-blocked). Each active or blocked linked issue surfaced in the summary SHALL provide a direct navigation link to that issue's detail page, so a user can jump to the issue that is currently driving or holding the epic.

#### Scenario: Active issue links to its detail page
- **WHEN** the summary is rendered for an epic that has an active (in-progress) linked issue
- **THEN** the summary SHALL list that issue
- **AND** SHALL render a navigation link from that issue entry to the issue's detail page

#### Scenario: Blocked issue links to its detail page
- **WHEN** the summary is rendered for an epic that has a health-blocked linked issue
- **THEN** the summary SHALL list that blocked issue
- **AND** SHALL render a navigation link from that issue entry to the issue's detail page

#### Scenario: No active or blocked issues shows an empty state
- **WHEN** the summary is rendered for an epic with no active and no blocked linked issues
- **THEN** the current activity area SHALL render a clear empty state (e.g. no current activity)
- **AND** SHALL NOT render broken or placeholder issue links

### Requirement: Next issue summary with navigation and reason

The summary area SHALL display the next startable linked issue when one exists, and that entry SHALL link to the issue's detail page. When no linked issue is currently startable, the summary SHALL display the next-issue reason explaining why advancement is not progressing, and where the blocker is a specific linked issue the summary SHALL allow navigating to that issue.

#### Scenario: Startable next issue links to its detail page
- **WHEN** the summary is rendered for an epic that has a startable next issue
- **THEN** the summary SHALL display that next issue
- **AND** SHALL render a navigation link from the next-issue entry to the issue's detail page

#### Scenario: No startable next issue shows a reason
- **WHEN** the summary is rendered for an epic with undelivered linked issues but no startable next issue
- **THEN** the summary SHALL display the next-issue reason
- **AND** SHALL NOT present a non-existent next issue as startable

#### Scenario: Waiting reason navigates to the relevant issue
- **WHEN** the summary is rendered for a running epic whose advancement is waiting on a specific in-progress linked issue
- **THEN** the waiting reason SHALL identify the relevant issue
- **AND** SHALL provide a navigation link to that issue's detail page

### Requirement: Distinct advancement-status copy

The summary area SHALL present clear, distinct copy for each advancement state so a user can tell at a glance why the epic is or is not progressing. The summary SHALL distinguish at least the following states: a running epic that is idle because no next issue is startable (running-but-idle); a running epic waiting for an in-progress linked issue to finish; a next issue that is not startable because it is in draft; and a next issue blocked by an external prerequisite. The copy SHALL NOT conflate these states into a single generic message.

#### Scenario: Running-but-idle copy
- **WHEN** the summary is rendered for a running epic that has no startable next issue
- **THEN** the summary SHALL display copy identifying the running-but-idle state
- **AND** SHALL explain that the epic is running but nothing is currently advancing

#### Scenario: Waiting for in-progress issue copy
- **WHEN** the summary is rendered for a running epic that is waiting for an in-progress linked issue to finish
- **THEN** the summary SHALL display copy indicating it is waiting for the in-progress issue
- **AND** SHALL identify the in-progress issue

#### Scenario: Draft blocker copy
- **WHEN** the summary is rendered for an epic whose next candidate issue is in draft and therefore not startable
- **THEN** the summary SHALL display copy indicating the next issue is a draft and cannot be started yet

#### Scenario: External prerequisite blocker copy
- **WHEN** the summary is rendered for an epic whose next candidate issue is blocked by an external prerequisite
- **THEN** the summary SHALL display copy indicating the external prerequisite blocker
- **AND** SHALL NOT present the issue as merely idle or finished

### Requirement: Idle epic advancement visibility

For an idle epic, the summary SHALL indicate whether a startable next issue exists. When no startable next issue exists, the summary SHALL explain why (e.g. next issue is draft, externally blocked, or no linked issues), so a user can decide whether starting the epic would make progress.

#### Scenario: Idle epic with a startable next issue
- **WHEN** the summary is rendered for an idle epic that has a startable next issue
- **THEN** the summary SHALL indicate that a startable next issue exists
- **AND** SHALL display that next issue with a navigation link

#### Scenario: Idle epic with no startable next issue explains why
- **WHEN** the summary is rendered for an idle epic whose linked issues are not currently startable
- **THEN** the summary SHALL explain why no issue is startable
- **AND** SHALL NOT imply the epic is advancing

### Requirement: Paused epic reason and resume hint

For a paused epic, the summary SHALL display the pause reason and SHALL state that resuming the epic will re-evaluate advancement (readiness and the next startable issue), so the user understands what Resume will do before invoking it.

#### Scenario: Paused epic shows pause reason
- **WHEN** the summary is rendered for a paused epic that has a pause reason
- **THEN** the summary SHALL display the pause reason

#### Scenario: Paused epic shows resume re-evaluation hint
- **WHEN** the summary is rendered for a paused epic
- **THEN** the summary SHALL state that resuming will re-evaluate advancement
- **AND** SHALL NOT imply Resume merely continues a previously selected issue without re-evaluation

### Requirement: Description region is demoted and collapsible

The full epic description SHALL be rendered as a secondary Overview/Description region placed after the summary area. The Overview/Description region SHALL be collapsible so a user can focus on the summary without the long description occupying the first fold.

#### Scenario: Description is collapsed or collapsible under Overview
- **WHEN** the Epic detail page is rendered for an epic with a long description
- **THEN** the description SHALL appear in an Overview/Description region after the summary
- **AND** the region SHALL be collapsible by the user

#### Scenario: Description region is absent when there is no description
- **WHEN** the Epic detail page is rendered for an epic with an empty description
- **THEN** no Overview/Description region SHALL be rendered

### Requirement: Summary reordering does not regress existing detail capabilities

Reordering the detail page to a summary-first information architecture SHALL NOT remove or break the existing epic detail capabilities: listing and managing linked issues, editing the epic, and adding linked issues. These capabilities SHALL remain reachable and functional after the summary area is introduced.

#### Scenario: Linked issues listing remains available
- **WHEN** the Epic detail page is rendered with the summary-first layout
- **THEN** the linked issues listing SHALL remain present and functional
- **AND** editing, adding, and per-linked-issue actions SHALL continue to work

#### Scenario: Edit epic remains available
- **WHEN** the Epic detail page is rendered with the summary-first layout
- **THEN** the edit-epic action SHALL remain reachable and functional
