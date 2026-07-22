### Requirement: Empty transcript states identify the observed cause

When a session transcript has no visible content, the session page SHALL distinguish between a logical session for which no content has ever been received and a runtime-filtered view for which the logical session has recorded content under another runtime. The page MUST derive the diagnosis from available transcript and session evidence and MUST NOT describe a runtime-filtered result as a session with no recorded activity.

#### Scenario: Running session has never received content
- **WHEN** a running session has no visible transcript content and no evidence of recorded content in any runtime
- **THEN** the page SHALL state that the session has started but no content has been received
- **AND** the page MUST NOT claim that content was filtered by a runtime mismatch

#### Scenario: Terminal session has never received content
- **WHEN** a terminal session has no visible transcript content and no evidence of recorded content in any runtime
- **THEN** the page SHALL state that no content was received for the session
- **AND** the page MUST NOT present the session as still waiting for activity

#### Scenario: Selected runtime excludes recorded content
- **WHEN** the selected runtime view has no visible transcript content but the logical session has recorded content associated with another runtime
- **THEN** the page SHALL state that the current runtime has no content
- **AND** the page SHALL indicate that content is available from a historical runtime

#### Scenario: Empty cause cannot be proven as a runtime mismatch
- **WHEN** a transcript is empty and the available evidence does not establish that another runtime contains recorded content
- **THEN** the page MUST NOT diagnose a runtime mismatch
- **AND** the page SHALL use the no-content-received state appropriate to the session status

### Requirement: Runtime-filtered empty states provide a history action

When recorded content exists under another runtime, the empty state SHALL provide an actionable way to view an available historical runtime within the same logical session. The action MUST preserve the logical session context and MUST NOT navigate to a different session.

#### Scenario: Historical runtime is available
- **WHEN** the current runtime view is empty and runtime lineage identifies a historical runtime with recorded content
- **THEN** the empty state SHALL offer an action to view that historical runtime
- **AND** activating the action SHALL open the historical runtime transcript for the same logical session

#### Scenario: No historical runtime is available
- **WHEN** the logical session has never received content and no historical runtime with content is available
- **THEN** the empty state MUST NOT offer a history-switch action
