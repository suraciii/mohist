# Terminal report settlement

## Scenarios

### Accepted reports are durably tracked

- **WHEN** a report matches the active task/work/runner identity and passes any Agent execution binding fence
- **THEN** the Workflow commits the terminal task transition before returning `accepted`
- **AND** the Runner-facing response contains `tracked: true`
- **AND** the task result remains eligible for normal Workflow advancement

### Stale reports are not tracked

- **WHEN** a report is rejected as stale, including a worker mismatch, binding mismatch, deadline race, or missing attempt
- **THEN** the response contains `tracked: false`
- **AND** no Artifact binding, follow-up task, terminal transition, or journal deletion is implied

### Identical response-loss replay is idempotently accepted

- **WHEN** the first accepted terminal response is lost after the Workflow commits
- **AND** the Runner replays the same result for the same taskRunId, workId, worker, and Agent binding
- **THEN** the Workflow returns `accepted` without appending duplicate terminal events or rebinding Artifacts
- **AND** the response contains `tracked: true`

### Conflicting replay remains stale

- **WHEN** a terminal attempt receives a different result fingerprint, artifact set, follow-up list, worker, or Agent binding
- **THEN** the report returns `stale` and `tracked: false`
- **AND** the previously committed result remains unchanged

### Accepted settlement does not become unconfirmed

- **WHEN** a valid Agent terminal report is accepted before its settlement deadline
- **THEN** the task reaches its terminal state
- **AND** no `agent-result-unconfirmed` event is emitted for that attempt
