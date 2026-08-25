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

Implementation reviewed: the strict API mapping, canonical fingerprint propagation, immutable terminal Agent binding, exact terminal attempt lookup, replay fence, and partial-class concern splits remain limited to the existing report boundary. The fingerprint is carried from the original WorkResult rather than reconstructed from normalized TaskReport fields; malformed-output failures retain the original fingerprint when safe.

Validation completed on 2026-08-24:

- `npm ci` passed in the isolated worktree.
- `npm run verify` passed after the report route and binding helper splits.
- Repository summary: passed.
- Unit: 4252 passed.
- Spec: 2869 passed.
- Architecture: 54 passed.
- Web and Runner Vitest suites passed.
- CLI tests: 2000 passed.
- Go Slack tests passed.
- File-size ratchet passed against `cbdd5b718`.
- Focused MTP apphost runs passed: WorkflowArtifactBindingSpecs 22/22, RunnerPollRecoveryStateApiSpecs 8/8, AgentResultSettlementSpecs 18/18, and old-state persistence 1/1.
- `git diff --check` passed.

The prior broad run before the final file-size split was superseded by this clean canonical gate; no deployment or merge was performed.
