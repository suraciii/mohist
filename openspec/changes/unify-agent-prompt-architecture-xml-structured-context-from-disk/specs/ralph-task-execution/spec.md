### Requirement: Build task prompt uses XML structure with file references

The `buildTaskContext()` function in `context-assembler.ts` SHALL be rewritten to use `formatAgentPrompt()`.

Proposal and design content SHALL NOT be inlined in the prompt. Instead, they SHALL be listed as `<context-files>` entries with absolute file paths and descriptions.

The spec (referenced by `task.spec`) SHALL remain inline as `<spec>` content.

#### Scenario: Proposal and design as file references

- **WHEN** buildTaskContext is called for task T-003 and proposal.md and design.md exist in the change directory
- **THEN** the output SHALL contain `<context-files>` with two `<file>` elements pointing to proposal.md and design.md
- **AND** the output SHALL NOT contain the full text of proposal.md or design.md inline

#### Scenario: Spec stays inline

- **WHEN** task T-003 references `specs/auth/spec.md#REQ-002`
- **THEN** the full content of that spec SHALL appear inline within `<spec>` tags

#### Scenario: Agent role and contract

- **WHEN** buildTaskContext is called for task T-003 of 5 total tasks for issue #42
- **THEN** `<role>` SHALL contain "You are implementing task T-003 of 5 for issue #42"
- **AND** `<contract>` SHALL contain commit instructions as the first item

### Requirement: Learnings as file references

Previous task learnings SHALL be listed as `<context-files>` entries pointing to `{changeDir}/session-memories/*.json`.

The learnings SHALL NOT be inlined in the prompt text.

#### Scenario: Learnings referenced but not inlined

- **WHEN** tasks T-001 and T-002 have completed with learnings stored in session-memories/
- **THEN** the prompt for T-003 SHALL contain `<context-files>` entries for the learning JSON files
- **AND** the prompt SHALL NOT contain the text content of those learnings inline

### Requirement: WIP resume and retry context remain inline

When a task is retried after timeout with WIP commit, the WIP resume context SHALL be inlined within `<task>` as additional context.

When a task is retried after failure, the failure reason SHALL be inlined within `<task>`.

#### Scenario: WIP resume context inline

- **WHEN** task T-003 timed out with a WIP commit containing changes to src/auth.ts
- **THEN** the retry prompt SHALL inline the WIP resume context (changed files, diff stat) within `<task>`
