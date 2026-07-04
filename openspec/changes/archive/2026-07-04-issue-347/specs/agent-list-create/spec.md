### Requirement: Create-agent entries open the editor dialog

The `/agents` list page MUST provide agent creation through the `AgentProfileEditor`
dialog and MUST NOT navigate to any route when either create entry point is
activated. Both the "New Agent" header button (testid `agent-list-create`) and the
empty-state "Create Agent" button (testid `agents-empty-create`) SHALL open the
`AgentProfileEditor` in create mode — i.e. mounted with `agent === null` and
exposed to the DOM under testid `agent-profile-editor`. Activating either entry
MUST NOT change the URL or emit a navigation, and the path `/agents/new` MUST NOT
be requested by these interactions.

#### Scenario: Header "New Agent" button opens the editor in create mode

- **WHEN** the user clicks the "New Agent" button (testid `agent-list-create`) on the `/agents` list page
- **THEN** the `AgentProfileEditor` dialog appears in the DOM (testid `agent-profile-editor`)
- **AND** the editor is mounted in create mode (no existing agent is being edited)
- **AND** the browser URL is unchanged and no navigation to `/agents/new` occurs

#### Scenario: Empty-state "Create Agent" button opens the editor in create mode

- **WHEN** the agent list is empty and the user clicks the "Create Agent" button (testid `agents-empty-create`) in the empty state
- **THEN** the `AgentProfileEditor` dialog appears in the DOM (testid `agent-profile-editor`)
- **AND** the editor is mounted in create mode (no existing agent is being edited)
- **AND** the browser URL is unchanged and no navigation to `/agents/new` occurs

### Requirement: Created agent is reflected in the list and routed to its detail page

On a successful agent creation from the list page's dialog, the agent list SHALL
refresh itself to include the newly created agent without requiring a manual
reload, and the user SHALL be routed to the new agent's detail page. The list
page itself MUST NOT perform this navigation; the editor's existing create-mode
contract (`useCreateAgent` invalidating the `['agents']` query and navigating on
success) SHALL be the sole driver of both effects.

#### Scenario: Successful create refreshes the list and navigates to the new agent

- **WHEN** the user submits the create form in the editor opened from the list page and the create request succeeds
- **THEN** the `['agents']` query is invalidated so the list re-renders with the newly created agent present
- **AND** the user is navigated to `/agents/<new-agent-id>` (the new agent's detail page)

### Requirement: Reopening the editor starts from a clean form state

The editor SHALL be conditionally rendered (mounted only while open) rather than
persistently mounted on the list page, so that each open presents a fresh form
free of values or validation state left over from a previous open.

#### Scenario: Closing and reopening the dialog resets the form

- **WHEN** the user opens the editor, types into a field, closes the editor without submitting, and then opens it again from either create entry point
- **THEN** the reopened editor presents empty form fields with no carried-over input or validation errors
