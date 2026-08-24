# Self-review

## Scope

The change is limited to the Server Workflow report acknowledgement and its existing Runner journal contract. It does not alter presence, receipt deadlines, cleanup, Artifact storage, or task-log delivery.

## Invariants checked

- `tracked=true` is emitted only for `ReportAck.Accepted`.
- A stale or mismatched report produces no Workflow mutation and leaves Runner retry semantics intact.
- Replay identity includes the complete canonical WorkResult fingerprint, taskRunId, workId, worker, and Agent binding where applicable.
- Artifact IDs remain part of the canonical fingerprint and normal first-report binding path.
- Terminal replay does not call Artifact binding, follow-up projection, or event commit.
- Existing Agent result deadline and binding fences remain authoritative.

## Review result

Implementation reviewed: the strict API mapping, persisted terminal fingerprint, exact terminal attempt lookup, and Agent binding replay fence are limited to the existing report boundary. Focused Server build and filtered tests pass with web build disabled; full web-enabled build was not runnable before npm dependencies were installed.
