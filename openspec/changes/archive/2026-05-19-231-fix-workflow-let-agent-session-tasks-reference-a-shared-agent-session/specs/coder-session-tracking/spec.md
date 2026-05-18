## MODIFIED Requirements

### Requirement: Shared task prompts are tracked in one real coder session

Coder-session tracking SHALL represent multiple task prompts that use the same resolved agent session reference as one real coder session transcript containing multiple prompt-led blocks. Each participating task result SHALL report the real `acpSessionId` it used, and projections SHALL keep task progress separate from session transcript state.

#### Scenario: Plan artifact prompts share acpSessionId
- **WHEN** a fresh Plan run executes `proposal`, `specs`, `design`, `tasks`, and `self-review` with `agentSessionRef: "plan-artifacts"`
- **THEN** coder-session tracking SHALL expose one real Plan coder session for those executed tasks
- **AND** each executed task result SHALL reference the same real `acpSessionId`

#### Scenario: Restored tasks do not create empty session records
- **WHEN** a Plan artifact task is restored from checkpoint or disk and dispatched as a service-call completion
- **THEN** coder-session tracking SHALL NOT create or touch a coder session solely because that task's policy contains `agentSessionRef`

#### Scenario: Retry creates distinct tracked session
- **WHEN** a later stage attempt uses the same logical `agentSessionRef` as an earlier attempt
- **THEN** coder-session tracking SHALL expose a distinct real session for the later attempt
- **AND** it SHALL NOT append later prompts to the earlier attempt's completed transcript
