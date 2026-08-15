# Explicit Started-Work Replacement

## Requirement: Physical observations do not settle or replace work

The Workflow MUST persist a `TargetMissing` or `Unknown` observation against
the original Agent task tuple before an operator can request replacement. A
physical Session or Turn observation is not a Workflow result.

### Scenario: target is missing without a result receipt

- GIVEN a Workflow Agent TaskRun has an exact task, work, and Runner identity
- WHEN its bound runtime reports `TargetMissing` without a WorkResult receipt
- THEN the original TaskRun MUST remain running with an unresolved settlement
- AND the Workflow MUST NOT mark it completed or failed
- AND the Workflow MUST NOT create a replacement TaskRun or work item

### Scenario: idle or completed physical state has no result authority

- GIVEN a Workflow Agent TaskRun has no authoritative WorkResult receipt
- WHEN an AgentSession or AgentTurn is idle or physically completed
- THEN the Workflow MUST preserve the original unresolved identity
- AND it MUST NOT infer output, artifacts, follow-up tasks, success, failure,
  or replacement work

## Requirement: Only an explicit blocked-work command can create a replacement

The Server MUST create replacement work only through the authenticated,
run-scoped replacement command after the original settlement is blocked and
contains `Unknown` or `TargetMissing` as its persisted observation.

### Scenario: operator supersedes a blocked unknown attempt

- GIVEN an original Agent TaskRun is blocked with a persisted `Unknown` or
  `TargetMissing` observation
- WHEN an authenticated operator submits the exact original tuple, reason,
  request id, and explicit confirmation
- THEN the Server MUST retain an immutable supersession disposition on the old
  TaskRun
- AND it MUST create exactly one new pending TaskRun with a distinct identity
- AND it MUST allocate the replacement work id only through normal scheduling

### Scenario: late receipt for superseded identity

- GIVEN an original TaskRun was superseded and a new TaskRun was created
- WHEN a Runner reports a receipt with the old task, work, and Runner tuple
- THEN the Server MUST acknowledge it as stale
- AND it MUST NOT change the replacement status, output, artifacts, or
  follow-up tasks

### Scenario: replacement request replay

- GIVEN a replacement command has committed for an original tuple and request
  id
- WHEN the same actor repeats the same request
- THEN the Server MUST return the original replacement result without creating
  another TaskRun
- AND a request-id replay with a different fingerprint MUST be rejected
