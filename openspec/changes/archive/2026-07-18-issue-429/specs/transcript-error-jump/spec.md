### Requirement: The session tool-error evidence bar is activatable to jump to the first failed tool call

When the session-level tool-error evidence region (`data-testid="session-errors-region"`) is visible and at least one failed tool call exists in the transcript, the region SHALL be activatable (pointer click and keyboard). Activating it SHALL locate the first failed tool row in document order via its existing `data-tool-state="failed"` anchor, scroll it into view, and apply a transient highlight to that row. The activation target SHALL be the first failed tool call in document order, independent of which turn contains it.

#### Scenario: Activating the evidence bar jumps to the first failed tool call
- **WHEN** the session-errors region is visible and the transcript contains one or more tool rows with `data-tool-state="failed"`
- **AND** a user activates the session-errors region
- **THEN** the first failed tool row in document order SHALL be scrolled into view
- **AND** that row SHALL receive a transient highlight

#### Scenario: Activation locates the first failure across multiple turns
- **WHEN** failed tool rows exist in more than one turn
- **AND** a user activates the session-errors region for the first time
- **THEN** the first failed tool row in document order SHALL be the locate target, regardless of which turn contains it

#### Scenario: Evidence bar is keyboard activatable
- **WHEN** a user focuses the session-errors region and activates it via the keyboard
- **THEN** the locate-and-highlight behavior SHALL occur identically to pointer activation

### Requirement: A next-error affordance iterates failures in document order

When the transcript contains more than one failed tool call, the session-errors region SHALL expose a next-error affordance. Activating next-error SHALL locate the next failed tool row after the currently-targeted one in document order, scrolling it into view and applying a transient highlight. Repeatedly activating next-error SHALL cycle through all failed tool rows in document order. When the currently-targeted failure is the last one, next-error SHALL wrap to the first failed tool row.

#### Scenario: Next-error moves to the subsequent failure
- **WHEN** the session-errors region has already located one failed tool row
- **AND** at least one failed tool row follows it in document order
- **AND** a user activates next-error
- **THEN** the next failed tool row in document order SHALL be scrolled into view and highlighted

#### Scenario: Next-error wraps from last failure back to first
- **WHEN** the currently-targeted failed tool row is the last one in document order
- **AND** a user activates next-error
- **THEN** the first failed tool row in document order SHALL be scrolled into view and highlighted

#### Scenario: Next-error affordance is absent when only one failure exists
- **WHEN** the transcript contains exactly one failed tool call
- **THEN** the session-errors region SHALL NOT expose a next-error affordance

### Requirement: The existing count, category, and reason display is preserved

Activating the session-errors region or the next-error affordance SHALL NOT change the existing count, failure-category badge, or failure-reason text rendered by the session-errors region. The bar SHALL continue to render when `statusKind === 'failed'`, or a `failureCategory` is present, or `toolErrorCount > 0`, and SHALL continue to read its data from the existing `meta.eventSummary.toolErrorCount` / `meta.eventSummary.failureCategory` / `meta.failureReason` fields without introducing new data-source fields.

#### Scenario: Count and category remain unchanged after activation
- **WHEN** a user activates the session-errors region or the next-error affordance
- **THEN** the tool-error count, failure-category badge, and failure-reason text SHALL remain unchanged

#### Scenario: Evidence bar does not render when there is no error evidence
- **WHEN** the session is not failed, has no failure category, and has zero tool errors
- **THEN** the session-errors region SHALL NOT render

### Requirement: Error navigation does not activate when no failed tool row is present

If `toolErrorCount > 0` is reported by the metadata but no `data-tool-state="failed"` row currently exists in the rendered transcript (for example during streaming, before the failed row has mounted), activation SHALL NOT throw and SHALL NOT scroll to an arbitrary row. The activation target SHALL resolve only to a present failed tool row.

#### Scenario: Activation is a no-op when no failed row is mounted
- **WHEN** the session-errors region is visible but no element matching `[data-tool-state="failed"]` is currently rendered in the transcript
- **AND** a user activates the region
- **THEN** no scroll SHALL occur
- **AND** no exception SHALL be raised
