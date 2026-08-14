# Runner Work-Result Recovery

## Requirement: durable result delivery

The Runner MUST durably persist a dispatch identity before executing it and
MUST durably persist a returned result before reporting it. The journal entry
MUST be removed only after the server confirms a durable Accepted or Stale
acknowledgement for that same identity.

### Scenario: process restarts after a result is produced

- WHEN the Runner restarts after a result is durably recorded and before the
  server acknowledgement is durable
- THEN it MUST report the recorded result with the original owner and work
  identity
- AND it MUST NOT invoke the action again

### Scenario: process restarts before a result exists

- WHEN the Runner restarts with only a `started` journal entry
- THEN it MUST refuse to execute that dispatch again
- AND it MUST leave Workflow outcome arbitration to the existing unresolved,
  authoritative-result, and explicit-stop paths

### Scenario: local journal persistence fails

- WHEN journal state cannot be read or atomically written
- THEN the Runner MUST gate new claims
- AND it MUST NOT report a settled result that has no durable local copy

### Scenario: server acknowledgement is lost

- WHEN the server accepts a result but the Runner cannot remove its journal
  entry
- THEN the Runner MUST retain and retry the exact result
- AND duplicate delivery MUST remain safe under the server's Accepted/Stale
  identity contract
