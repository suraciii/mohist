# Workflow Cleanup Turn

## Requirement: cleanup has a separate authorized turn

The Server MUST admit a Workflow cleanup operation as a separate, replay
idempotent Agent turn on the existing physical AgentSession. The cleanup turn
MUST NOT create a second Workflow execution binding for the original task.

### Scenario: cleanup follows a terminal Workflow turn

- GIVEN the original Workflow Agent turn has a terminal runtime observation
- AND the task attempt still owns the matching frozen execution binding
- WHEN Runner submits cleanup operation `n`
- THEN Server returns one stable cleanup input identity and Agent turn identity
- AND a replay of operation `n` returns those same identities
- AND the cleanup runtime events are accepted under that cleanup turn identity

### Scenario: cleanup admission is stale or conflicting

- WHEN the original binding does not match, the task is no longer eligible, or
  operation `n` conflicts with persisted cleanup state
- THEN Server rejects the operation
- AND it MUST NOT create or replace an Agent turn

## Requirement: cleanup delivery is ordered

The Runner MUST schedule cleanup admission after the original execution's
terminal runtime facts for the same logical Workflow Session have been
acknowledged. Cleanup admission MUST NOT be batched with terminal facts or
with another execution identity.
