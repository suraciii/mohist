# OpenSpec Capability Delta: workflow-artifact-ux

## ADDED Requirements

### Requirement: REQ-WAUX-001 Task row artifact chip visibility

Completed workflow tasks with one or more artifact summaries SHALL render clickable artifact chips on the task row, positioned between the task title and the duration. Tasks that are not completed or have no artifact summaries SHALL render no artifact chips.

#### Scenario: Completed task with artifacts displays chips
- **WHEN** a task has `status === "completed"` and `artifactSummaries.length > 0`
- **THEN** the task row SHALL display one artifact chip per summary after the task title and before the duration
- **AND** each chip SHALL show the artifact name with a file icon for files or a folder icon for directories
- **AND** each chip SHALL use subtle pill styling (e.g. `bg-blue-50 text-blue-700`)

#### Scenario: Incomplete or artifact-less task hides chips
- **WHEN** a task status is not `"completed"` or `artifactSummaries` is empty
- **THEN** the task row SHALL NOT display artifact chips

### Requirement: REQ-WAUX-002 Artifact chip opens viewer

Clicking an artifact chip SHALL open the `ArtifactContentViewer` dialog for the selected artifact.

#### Scenario: User clicks an artifact chip
- **WHEN** a user clicks an artifact chip on a completed task row
- **THEN** the `ArtifactContentViewer` dialog SHALL open
- **AND** the viewer SHALL display the selected artifact path or content

### Requirement: REQ-WAUX-003 Markdown artifact rendering

`ArtifactContentViewer` SHALL render artifacts whose path ends with `.md` or `.markdown` as formatted markdown using `react-markdown` with the `remarkGfm` plugin, matching the markdown rendering pattern used for issue descriptions.

#### Scenario: Markdown artifact is rendered
- **WHEN** the viewer displays an artifact with a `.md` or `.markdown` extension
- **THEN** the content SHALL be rendered with proper headings, lists, code blocks, and tables
- **AND** code blocks SHALL retain syntax highlighting
- **AND** the raw markdown source SHALL NOT appear as monospaced plain text

### Requirement: REQ-WAUX-004 Non-markdown artifact rendering

Non-markdown text artifacts (e.g. `.json`, `.yaml`, `.txt`) SHALL render as plain text inside a `<pre className="font-mono">` element.

#### Scenario: Non-markdown text artifact is rendered
- **WHEN** the viewer displays a text artifact whose path does not end with `.md` or `.markdown`
- **THEN** the raw content SHALL appear inside a `<pre>` element with monospaced font styling
- **AND** the content SHALL NOT be interpreted as markdown

### Requirement: REQ-WAUX-005 Artifact viewer header file size

The `ArtifactContentViewer` header SHALL display the artifact file size.

#### Scenario: File artifact header shows size
- **WHEN** the viewer opens a file artifact
- **THEN** the header SHALL show the artifact name or path alongside its size
- **AND** the size SHALL be formatted for readability

### Requirement: REQ-WAUX-006 Copy feedback

`ArtifactContentViewer` SHALL display a transient "Copied" feedback indicator after the user copies artifact content.

#### Scenario: User copies artifact content
- **WHEN** the user copies content from the artifact viewer
- **THEN** the viewer SHALL show a "Copied" indicator
- **AND** the indicator SHALL clear after a short delay or on the next user action

### Requirement: REQ-WAUX-007 Directory artifact summary

For directory artifacts, `ArtifactContentViewer` SHALL display the total file count and total size in the header while preserving existing directory browsing behavior.

#### Scenario: Directory artifact header shows aggregate info
- **WHEN** the viewer opens a directory artifact
- **THEN** the header SHALL show the directory name, total file count, and total size
- **AND** the user SHALL still be able to browse files within the directory
