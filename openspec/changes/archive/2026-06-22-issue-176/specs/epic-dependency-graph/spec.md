## ADDED Requirements

### Requirement: Dependency Graph View Toggle on the Epic Detail Page

The Epic detail page SHALL provide a dependency graph view alongside the existing Linked Issues list, selectable via a view toggle. The Linked Issues list view SHALL be preserved and SHALL NOT be replaced or removed by the graph view. Switching between list and graph SHALL NOT modify any Epic or issue data. The graph view SHALL be optional to render; when it is not rendered, the list view SHALL remain available.

#### Scenario: Graph and list are switchable

- **WHEN** a user opens an Epic detail page that has linked issues
- **THEN** the page SHALL offer a control to switch between the Linked Issues list view and the dependency graph view
- **AND** selecting either view SHALL display that view without altering Epic or issue data

#### Scenario: List view is preserved when the graph is introduced

- **WHEN** the dependency graph view is added to the Epic detail page
- **THEN** the Linked Issues list view SHALL remain available as an alternative
- **AND** the list view SHALL NOT be removed or replaced by the graph

### Requirement: Graph Degrades to List for Small Epics

The dependency graph SHALL NOT be rendered when the Epic has zero or one linked issues. In those cases the Epic detail page SHALL present the Linked Issues list view instead of an empty or trivial graph, and SHALL NOT offer a broken or empty graph surface.

#### Scenario: Epic with no linked issues does not render a graph

- **WHEN** an Epic has zero linked issues
- **THEN** the dependency graph SHALL NOT be rendered
- **AND** the Linked Issues list view SHALL be presented

#### Scenario: Epic with a single linked issue does not render a graph

- **WHEN** an Epic has exactly one linked issue
- **THEN** the dependency graph SHALL NOT be rendered
- **AND** the Linked Issues list view SHALL be presented

### Requirement: Issue Nodes Colored by Status with Readiness Markers

Each linked issue in the Epic SHALL render as one node in the dependency graph. Each node SHALL be colored according to the issue's execution `Status` (`backlog`, `in_progress`, `done`, `cancelled`). Each node SHALL additionally carry a readiness marker that distinguishes exactly four states: **can start** (derived `CanStart` is true and the issue is not yet in progress), **waiting** (derived `Blocker` is `WaitingFor(Issue)`), **in progress** (the issue's `Status` is `in_progress`), and **done** (the issue's `Status` is `done`). A node whose readiness is **waiting** SHALL identify the issue number (`#N`) it is waiting on.

#### Scenario: Node is colored by execution status

- **WHEN** a linked issue is rendered as a node
- **THEN** the node SHALL be colored according to its execution `Status` (`backlog`, `in_progress`, `done`, or `cancelled`)

#### Scenario: Readiness marker distinguishes the four readiness states

- **WHEN** the graph renders linked issues
- **THEN** each node SHALL display a readiness marker of exactly one of: can start, waiting, in progress, or done
- **AND** the marker SHALL be derived from the issue's `CanStart`, `Blocker`, and `Status` rather than authored separately

#### Scenario: A waiting node identifies the blocking issue

- **WHEN** a node's readiness is waiting
- **THEN** the node SHALL identify the issue number (`#N`) of the undelivered prerequisite it is waiting on
- **AND** the identifier SHALL correspond to an edge in the graph so the blocking relationship is traceable

### Requirement: Directed Prerequisite Edges with Client-Side Layout

The dependency graph SHALL render a directed edge from each prerequisite issue to the issue that depends on it, derived from the linked issues' `prerequisiteNumbers`. Edge layout SHALL be computed on the client; the system SHALL NOT require a server-side layout service for epic-scoped graphs.

#### Scenario: Edge direction points from prerequisite to dependent

- **WHEN** linked issue B declares linked issue A as a prerequisite
- **THEN** the graph SHALL render a directed edge from node A to node B
- **AND** the edge SHALL represent the `prerequisiteNumbers` relationship

#### Scenario: Layout is computed client-side

- **WHEN** the dependency graph is rendered for an Epic
- **THEN** node and edge layout SHALL be computed on the client
- **AND** the system SHALL NOT depend on a server-side layout endpoint for epic-scoped graphs

### Requirement: Node Click Navigates to the Issue

Activating a linked-issue node in the dependency graph SHALL navigate the user to that issue. The navigation target SHALL be the issue represented by the node.

#### Scenario: Clicking a node opens the issue

- **WHEN** a user activates a node representing a linked issue
- **THEN** the user SHALL be navigated to that issue
- **AND** the navigation target SHALL be the issue the node represents

### Requirement: External Prerequisites Are Visually Distinct

A prerequisite issue that is not a member of the current Epic (an external prerequisite) SHALL be rendered in a way that is visually distinct from Epic member nodes (for example, a ghost or annotated node), so that it SHALL NOT be misread as an orphaned Epic issue. The distinct rendering SHALL apply only to prerequisites outside the Epic membership and SHALL NOT alter the rendering of Epic member nodes.

#### Scenario: External prerequisite renders distinctly from Epic members

- **WHEN** a linked issue has a prerequisite issue that is not a member of the same Epic
- **THEN** the graph SHALL render that prerequisite with a visual treatment distinct from Epic member nodes
- **AND** the treatment SHALL make clear that the prerequisite is external to the Epic

#### Scenario: Internal prerequisites render as normal member nodes

- **WHEN** a linked issue has a prerequisite issue that is also a member of the same Epic
- **THEN** the prerequisite SHALL render as a normal Epic member node
- **AND** it SHALL NOT receive the external-prerequisite visual treatment

### Requirement: Cycle Detection Falls Back to the List

If a cycle is detected in the rendered prerequisite graph, the dependency graph SHALL fall back to the Linked Issues list view instead of rendering a broken layout. The system SHALL assume the prerequisite graph is acyclic under normal domain guarantees and SHALL only trigger this fallback when an actual cycle is detected.

#### Scenario: Detected cycle falls back to the list

- **WHEN** the prerequisite relationships among an Epic's linked issues contain a cycle
- **THEN** the dependency graph SHALL NOT render a broken layout
- **AND** the Epic detail page SHALL present the Linked Issues list view instead

### Requirement: Dependency Graph Is a Read-Only Projection

The dependency graph SHALL be a read-only projection of existing prerequisite and readiness data. The graph SHALL NOT provide any affordance to create, edit, reorder, or delete prerequisite relationships, issue status, or readiness state. Starting an issue from the graph is out of scope; start capability is provided elsewhere and the graph SHALL NOT introduce a duplicate start control.

#### Scenario: No dependency editing affordance on the graph

- **WHEN** the dependency graph is rendered
- **THEN** the graph SHALL NOT offer any control to add, edit, reorder, or remove a prerequisite relationship
- **AND** the graph SHALL NOT mutate issue status, readiness, or prerequisite data

#### Scenario: No duplicate start control on graph nodes

- **WHEN** a node representing a startable issue is rendered
- **THEN** the graph SHALL NOT present a start-issue control on the node
- **AND** starting issues SHALL remain the responsibility of the existing start surface
