### Requirement: A mini timeline plots the session as activatable nodes

The session page SHALL render a compact mini timeline navigation surface that plots the session as a sequence of activatable nodes. Each node SHALL correspond to either a turn boundary or a within-turn key event. Activating any node SHALL locate the corresponding transcript row via its existing stable anchor and scroll it into view. The mini timeline SHALL coexist with the transcript without narrowing the transcript's full-width single-column invariant established for the flat timeline.

#### Scenario: Mini timeline renders alongside the transcript
- **WHEN** the session page renders with at least one turn
- **THEN** a mini timeline SHALL render as part of the session page
- **AND** the transcript SHALL retain its full-width single-column layout

#### Scenario: Activating a node scrolls the corresponding row into view
- **WHEN** a user activates a mini timeline node
- **THEN** the transcript row corresponding to that node SHALL be scrolled into view
- **AND** the row SHALL be located via its existing stable anchor (`data-turn-id` for turn nodes, `data-tool-call-id` for event nodes)

#### Scenario: Mini timeline nodes are keyboard activatable
- **WHEN** a user focuses a mini timeline node and activates it via the keyboard (for example Enter or Space)
- **THEN** the corresponding transcript row SHALL be scrolled into view identically to pointer activation

### Requirement: Mini timeline nodes derive from turns and three event kinds

The mini timeline SHALL plot two kinds of nodes. Turn nodes mark each turn boundary. Event nodes mark individual key events within the session and SHALL be classified into exactly three kinds, each visually distinct: a **failed tool call** (red), a **file-changing tool call** (green), and an **exploratory read-only tool call** (gray). Event nodes SHALL be derived from the existing display projection (`DisplayToolPart.status`, `DisplayToolPart.changedFiles`, and the tool's verb family); no new data model field SHALL be introduced to classify them.

#### Scenario: Failed tool call renders a red event node
- **WHEN** a tool call with state `failed` exists in the transcript
- **THEN** the mini timeline SHALL render a distinct failed-event node (red) at that call's position

#### Scenario: File-changing tool call renders a green event node
- **WHEN** a completed tool call that modifies one or more files exists in the transcript
- **THEN** the mini timeline SHALL render a distinct file-change event node (green) at that call's position

#### Scenario: Exploratory read-only tool call renders a gray event node
- **WHEN** a completed exploratory read-only tool call (for example read, grep, glob, search) exists in the transcript
- **THEN** the mini timeline SHALL render a distinct exploratory-read event node (gray) at that call's position

#### Scenario: Completed non-edit non-read tool calls do not produce event nodes
- **WHEN** a completed tool call neither fails, modifies files, nor belongs to the exploratory-read verb family
- **THEN** the mini timeline SHALL NOT render a dedicated event node for that call

### Requirement: Mini timeline remains meaningful for single-turn sessions

When the session has exactly one turn, the mini timeline SHALL still derive event nodes (failed / file-change / exploratory read) from that turn's contents so that a single-turn long session yields useful navigation. A single-turn session with at least one qualifying event SHALL NOT collapse to a single featureless node.

#### Scenario: Single-turn session with events produces event nodes
- **WHEN** the session has exactly one turn and that turn contains at least one failed, file-changing, or exploratory read-only tool call
- **THEN** the mini timeline SHALL render one event node per qualifying call
- **AND** SHALL NOT render only a single turn node

#### Scenario: Activating a single-turn session event node locates the underlying row
- **WHEN** a user activates an event node in a single-turn session
- **THEN** the corresponding tool row SHALL be scrolled into view

### Requirement: Event nodes descend into context groups to the underlying tool call

Because consecutive exploratory calls are collapsed into a `context-group` row by the flat-timeline projection, an event node whose underlying tool call lives inside such a group SHALL still map to that inner call's `data-tool-call-id` rather than to the group wrapper. Activating such a node SHALL locate the inner tool row; if the containing group is collapsed, the group SHALL be expanded as part of the locate action so that the target row is visible on screen.

#### Scenario: Event node inside a context group targets the inner tool call
- **WHEN** a mini timeline event node corresponds to a tool call that lives inside a collapsed `context-group` row
- **AND** a user activates that node
- **THEN** the containing context group SHALL be expanded
- **AND** the inner tool row matching the node's underlying `data-tool-call-id` SHALL be scrolled into view

#### Scenario: Failed call inside a context group is reachable from the mini timeline
- **WHEN** a tool call with state `failed` lives inside a context-group row
- **THEN** the mini timeline SHALL render a failed-event node for that call
- **AND** activating the node SHALL expand the containing group and locate the failed inner row
