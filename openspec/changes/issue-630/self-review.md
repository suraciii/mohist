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

Implementation reviewed: the strict API mapping, canonical fingerprint propagation, immutable terminal Agent binding, exact terminal attempt lookup, and replay fence remain limited to the existing report boundary. The fingerprint is carried from the original WorkResult rather than reconstructed from normalized TaskReport fields; malformed-output failures retain the original fingerprint when safe. Focused Server build passes with web build disabled. The filtered SpecTests run was not clean because unrelated pre-existing shared-fixture failures remain; the exact failures and environment limitations are recorded in the handoff rather than hidden.
