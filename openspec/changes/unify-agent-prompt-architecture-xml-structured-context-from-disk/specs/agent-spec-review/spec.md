### Requirement: Plan and review prompts use formatAgentPrompt

All `buildXxxPrompt()` functions in `artifact-prompt.ts` SHALL be rewritten to use `formatAgentPrompt()`.

Plan stage prompts (buildArtifactPrompt) SHALL include `<project_context>` from agent config.

Review stage prompts (buildReviewerPrompt, buildSelfReviewPrompt, buildReviewSelfCheckPrompt, buildAutoFixPrompt, buildReVerifyPrompt) SHALL use `formatAgentPrompt()` with appropriate role, task, contract, and instruction.

Conflict resolution prompt SHALL use `formatAgentPrompt()`.

#### Scenario: Plan proposal prompt with project context

- **WHEN** buildArtifactPrompt is called for artifact type 'proposal'
- **THEN** the output SHALL contain `<project_context>` from agent config
- **AND** the output SHALL contain `<task>`, `<template>`, `<instruction>` sections

#### Scenario: Review prompt with project context

- **WHEN** buildReviewerPrompt is called
- **THEN** the output SHALL use `<mohist-task>` envelope
- **AND** the output SHALL contain `<project_context>` from agent config
- **AND** the existing review instruction content SHALL be inside `<instruction>`

#### Scenario: Auto-fix prompt with contract

- **WHEN** buildAutoFixPrompt is called
- **THEN** `<contract>` SHALL contain "Apply ONLY the fixes described" and "Do NOT modify review.md"
