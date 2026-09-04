## Requirement: Launch admission enforces task scope

The Server SHALL reject a project Agent launch before creating a Session or
Job when the Agent purpose is missing or when the launch context requires a
permission not covered by the Agent declaration.

### Scenario: Missing purpose is rejected without durable effects

- **WHEN** a project Agent with a blank purpose is launched
- **THEN** the Server returns `agent_task_scope_rejected` with
  `purpose_missing`
- **AND** no AgentSession, AgentJob, attachment binding, or Runner claim is
  created

### Scenario: Context permission is rejected without durable effects

- **WHEN** a launch includes an Issue, Epic, repository, or workspace context
  that is not covered by the corresponding declared read/write permission
- **THEN** the Server returns `agent_task_scope_rejected` with
  `permission_missing` and the missing terms
- **AND** no AgentSession or AgentJob is created

### Scenario: A covered context is frozen as a launch fact

- **WHEN** a non-archived Agent with a purpose and matching permissions is
  launched with context
- **THEN** the launch is accepted through the normal shared launcher
- **AND** the Session definition and AgentJob dispatch definition contain the
  purpose, declared permissions, and inferred required permissions captured
  at launch

### Scenario: Purpose is not guessed from the prompt

- **WHEN** the purpose is present but the prompt uses unrelated natural
  language
- **THEN** the deterministic gate does not claim semantic compatibility
- **AND** a future explicit requested-capability contract owns that decision

## Requirement: All shared launcher paths use one gate

Manual, mention, routed, and Slack connection launches SHALL call the same
Server-owned gate before their first Session or Job write. An already accepted
idempotent replay SHALL return its original durable result and snapshot.

## Requirement: Workflow bypass is explicit

The `mohist/agent` Workflow Action SHALL be documented as a separate owner
boundary until its translator can consume the same frozen scope contract
before Workflow claim and Runner submission. It SHALL NOT introduce a second
permission vocabulary or silently rely on the AgentLauncher gate.
