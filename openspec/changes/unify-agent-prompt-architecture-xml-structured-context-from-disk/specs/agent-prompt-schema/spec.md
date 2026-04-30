### Requirement: Unified agent prompt XML format

The system SHALL provide a single function `formatAgentPrompt(parts)` that produces an XML-structured prompt string using the `<mohist-task>` envelope.

The function SHALL accept these fields:
- `role` (required): What this agent session is doing
- `projectContext` (optional): Project background injected as `<project_context>` with "do not include in output" annotation
- `rules` (optional): Per-stage constraints injected as `<rules>` with "do not include in output" annotation
- `contextFiles` (optional): Array of `{path, desc}` objects rendered as `<file>` elements inside `<context-files>`
- `spec` (optional): Task-specific requirements rendered inline as `<spec>`
- `task` (required): The task definition rendered as `<task>`
- `contract` (optional): Behavioral expectations rendered as `<contract>`
- `template` (optional): Output structure rendered as `<template>`
- `instruction` (optional): Schema-specific guidance rendered as `<instruction>`

The output SHALL use XML tags with semantic annotation comments (`<!-- ... -->`) to clarify the purpose of each section for the agent.

The output ordering SHALL be: `<role>`, `<project_context>`, `<rules>`, `<context-files>`, `<spec>`, `<task>`, `<contract>`, `<template>`, `<instruction>`.

#### Scenario: Build task prompt assembled via formatAgentPrompt

- **WHEN** formatAgentPrompt is called with role="You are implementing task T-003 of 5", projectContext="Tech stack: TypeScript", spec="POST /auth/login SHALL return 200", task="T-003: Implement login endpoint", contract="1. Implement\n2. Commit"
- **THEN** the output SHALL contain `<mohist-task>` as root element
- **AND** the output SHALL contain `<role>`, `<project_context>`, `<spec>`, `<task>`, `<contract>` sections in order
- **AND** `<project_context>` SHALL include a comment "Do NOT include this in your output"

#### Scenario: Plan artifact prompt assembled via formatAgentPrompt

- **WHEN** formatAgentPrompt is called with role="Create the proposal artifact", template="## Why\n...", instruction="Create the proposal document..."
- **THEN** the output SHALL contain `<template>` and `<instruction>` sections
- **AND** the output SHALL NOT contain `<spec>` or `<context-files>` sections
