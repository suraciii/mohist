## MODIFIED Requirements

### Requirement: REQ-WD-001 workflow tasks, checks, and reactions declare structured contracts

Workflow definitions SHALL support generic result contracts, self-repair policy, invalidation hints, and reaction input selectors for task/check/reaction orchestration.

#### Scenario: Task definition declares a structured result contract

- **WHEN** a task produces judgeable AI output
- **THEN** its definition MAY declare a `resultContract` with contract kind, required marker policy, allowed markers, item policy, and declared output source
- **AND** built-in judgment tasks SHALL default to a promise-marker contract when PASS/FAIL is judgeable

#### Scenario: Task definition declares self-repair boundaries

- **WHEN** a task implementation may repair during execution
- **THEN** its definition SHALL express `selfRepairPolicy` boundaries, allowed scopes, max attempts, verification requirements, and disallowed repair reasons
- **AND** checks SHALL NOT use this policy to modify files or start agents

#### Scenario: Reaction definition selects failed context

- **WHEN** a check failure schedules a reaction task
- **THEN** the reaction definition SHALL be able to select failed check output, selected task outputs, artifacts, structured item batches, snapshot metadata, and retry/recheck policy as explicit inputs
