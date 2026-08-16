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
- AND it MUST leave terminal Workflow outcome arbitration to the existing
  authoritative-result and explicit-stop paths

## Requirement: recovered Agent started fences report only an unconfirmed observation

When startup loads a durable `started` entry for a Workflow Agent task with its
original task-run and work identity, the Runner MUST report `status: unknown`
through the normal report route after connection. The report MUST retain the
original Workflow owner, task-run, work, and authenticated Runner identities.
It is an `agent-result-unconfirmed` observation only; it MUST NOT contain or
infer success, failure, output, artifacts, or replacement work.

The Runner MUST remove that started fence only after the Server durably
acknowledges the observation as Accepted or Stale. A failed delivery or local
delete MUST retain the fence and retry the same observation. Entries not loaded
at startup, non-Agent tasks, checks, or records missing the complete owner
identity MUST remain fences and MUST NOT be sent through this unknown-result
route.

### Scenario: recovered Agent started fence enters settlement without a terminal result

- WHEN a Runner restarts with a durable `started` entry for a Workflow Agent
  task and no completed receipt
- THEN it MUST report the original identity with `status: unknown`
- AND the Server MUST preserve the running task under the existing unknown or
  blocked settlement rather than write task success or failure
- AND the Runner MUST NOT execute the original dispatch again

### Scenario: recovered AgentJob started fence enters Unknown without terminal failure

- WHEN a Runner restarts with a durable `started` entry for an AgentJob
  dispatch and no completed receipt
- THEN it MUST report the original AgentJob and work identity with
  `status: unknown`
- AND the Server MUST validate the original Runner and work identity before
  moving the AgentJob to durable `Unknown`
- AND the Server MUST NOT write AgentJob `Failed` or `Completed`
- AND the Runner MUST NOT execute the original dispatch again

### Scenario: recovered AgentJob observation is idempotent

- WHEN the same unknown observation is delivered more than once
- THEN the first accepted observation MUST remain Unknown
- AND later delivery MUST be acknowledged as stale or idempotently accepted
- AND no second terminal transition or new work identity MUST be created

### Scenario: unknown observation acknowledgement is interrupted

- WHEN the Server accepts a recovered started-fence observation but the Runner
  cannot durably remove the local fence
- THEN the Runner MUST retain and redeliver the same non-terminal observation
- AND duplicate delivery MUST remain side-effect free under the original
  identity contract

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

## Requirement: recovered receipts preserve the original Workflow attempt

The Server SHALL accept a completed Runner journal receipt only through the
normal Workflow result-report path and only when its original task attempt,
work, and authenticated Runner identity match the persisted attempt. A matching
completed receipt MAY settle an unknown or blocked attempt exactly once. The
Server SHALL treat every later receipt for that terminal attempt as stale.

### Scenario: completed receipt arrives after the unresolved deadline

- WHEN a Runner reconnects with a completed journal entry for a Workflow Agent
  attempt that is already blocked for an unconfirmed result
- THEN the Server MUST apply that entry to the original task attempt
- AND it MUST use the receipt's authoritative success or failure rather than
  the earlier physical observation
- AND it MUST NOT create replacement work or a second terminal transition

## Requirement: started records are not terminal receipts

A Runner journal entry in `started` state, AgentSession idle/completed state,
AgentTurn terminal state, runtime close event, and terminal task-log ownership
are execution or log facts, not a complete Workflow result receipt. None SHALL
by itself produce task success or failure.

### Scenario: only a started record and terminal physical facts remain

- WHEN the original Runner process is lost after recording `started` but before
  durably recording a result
- AND the AgentSession later reports idle or completed
- THEN the Server MUST preserve the original unknown or blocked settlement
- AND it MUST NOT replay the physical dispatch or synthesize a task result

### Scenario: replacement execution is requested after result loss

- WHEN an operator uses explicit Workflow stop to abandon an unresolved attempt
  whose authoritative result is permanently unavailable
- AND a future product capability subsequently schedules replacement execution
- THEN any later execution MUST use a new task/work identity
- AND a late receipt for the abandoned identity MUST be stale and side-effect
  free
