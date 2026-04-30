### Requirement: Project agent config in workflow.yaml

The system SHALL support `agent` section in `workflow.yaml` with two optional fields:
- `context`: A multiline string containing project background (tech stack, build/test commands, conventions)
- `rules`: A mapping of stage names to arrays of constraint strings

The `loadAgentConfig()` function SHALL be added to `workflow-loader.ts` and return `{ context?: string, rules?: Record<string, string[]> }`.

When `workflow.yaml` does not contain an `agent` section, `loadAgentConfig()` SHALL return an empty object (no error).

#### Scenario: Load agent config from workflow.yaml

- **WHEN** workflow.yaml contains `agent.context` with "Tech stack: TypeScript" and `agent.rules.build` with ["Keep changes scoped"]
- **THEN** `loadAgentConfig()` SHALL return `{ context: "Tech stack: TypeScript", rules: { build: ["Keep changes scoped"] } }`

#### Scenario: Missing agent config

- **WHEN** workflow.yaml has no `agent` section
- **THEN** `loadAgentConfig()` SHALL return `{}`
- **AND** no error SHALL be thrown

#### Scenario: Agent config injected into prompt

- **WHEN** a build task prompt is assembled and agent config has `context` and `rules.build`
- **THEN** the prompt SHALL contain `<project_context>` with the config context
- **AND** the prompt SHALL contain `<rules>` with the build-stage rules
