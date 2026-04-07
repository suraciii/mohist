## ADDED Requirements

### Requirement: Explore page route
The system SHALL provide an `/explore/:id` route that displays the explore chat interface.

#### Scenario: Navigate to explore session
- **WHEN** user navigates to /explore/:id
- **THEN** system loads the session and displays the chat interface with message history

#### Scenario: Navigate to /explore without id
- **WHEN** user navigates to /explore
- **THEN** system redirects to the latest active session for the current project, or creates a new session if none exists

### Requirement: Explore header entry point
The system SHALL add an "Explore" button in the header that navigates to the explore page.

#### Scenario: Click explore button
- **WHEN** user clicks the Explore button in the header
- **THEN** user navigates to /explore

### Requirement: Chat message list
The system SHALL display explore messages in a scrollable list, with user and assistant messages visually distinguished.

#### Scenario: Display message history
- **WHEN** user opens an explore session
- **THEN** all persisted messages are displayed in chronological order with user and assistant messages visually distinct

#### Scenario: New message appears
- **WHEN** a new message is received (user sent or agent responded)
- **THEN** the message appears in the list and view auto-scrolls to the bottom

### Requirement: Tool call display
The system SHALL display agent tool calls as collapsible blocks showing tool name, arguments, and result.

#### Scenario: Display tool call
- **WHEN** agent invoked a tool during a response
- **THEN** system displays a collapsible block with tool name, and expandable sections for arguments and result

#### Scenario: Tool call default collapsed
- **WHEN** a tool call is displayed
- **THEN** it SHALL be collapsed by default, showing only the tool name

### Requirement: Streaming agent response
The system SHALL display agent responses as they stream in, updating the UI in real-time.

#### Scenario: Text streaming
- **WHEN** agent is generating a response
- **THEN** text appears incrementally as chunks arrive via SSE

#### Scenario: Tool call during streaming
- **WHEN** agent invokes a tool during streaming
- **THEN** tool call block appears immediately and the text stream continues after the tool result

### Requirement: Explore input
The system SHALL provide a text input for sending messages to the explore agent.

#### Scenario: Send message
- **WHEN** user types a message and presses Enter (or clicks Send)
- **THEN** message is sent to the API, user message appears in the chat, and streaming response begins

#### Scenario: Disabled during streaming
- **WHEN** agent is responding (streaming in progress)
- **THEN** the input is disabled to prevent concurrent messages

### Requirement: Markdown rendering
The system SHALL render agent responses as markdown, supporting code blocks, lists, and inline code.

#### Scenario: Render markdown content
- **WHEN** agent response contains markdown (code blocks, lists, headings, etc.)
- **THEN** system renders the markdown properly in the message

### Requirement: Navigate to created issue
The system SHALL provide a link to the created issue when an explore session is crystallized.

#### Scenario: Issue created from exploration
- **WHEN** agent creates an issue during exploration
- **THEN** system displays a link/button "View Issue #N" that navigates to /issue/:number
