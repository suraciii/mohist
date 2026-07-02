# Self Review Report

## Result: PASS

## Repaired Items

None. No safe repairs were required — the plan is internally consistent and complete.
Verification (cross-checked against the live codebase, not just the artifacts):

- **Server consumer surface is exhaustive.** `rg` over `packages/server/src` for `com.mohist.issue.(closed|work-completed)|IssueClosed|IssueWorkCompleted` returns exactly 11 files; all 11 are addressed by design D1–D3, with the historical `20260629120000_BackfillIssueCompletedAt.cs` correctly excluded as immutable history (D6).
- **Server test surface is exhaustive.** `rg` over `packages/server/tests` returns 9 files. `IssueQuerierSpecs.cs` reaches the legacy ids via the `IssueQuerier.WorkCompletedType`/`ClosedType` const accessors (a rename breaks compilation, so it is swept by T-001); `EpicProgressionSpecs.cs:114` carries a stale `IssueClosed` comment and is captured by T-001 acceptance criterion #5 ("the type names IssueClosed/IssueWorkCompleted no longer appear anywhere in server src or tests"). The proposal's "sweep all specs referencing …" clause subsumes both.
- **Web surface is exhaustive and correctly named.** The four cited web tests exist under the top-level `tests/` directory (`tests/canonical-event-types.test.ts`, `tests/live-task-cloud-event.test.tsx`) plus `src/app/providers/LiveTaskProvider.test.ts` and `src/app/providers/model/reverse-dns-outcome.test.ts`; all four reference the legacy keys/ids and require the sweep.
- **Spec anchors resolve.** Each `spec` URL in `tasks.json` slug-matches an actual `### Requirement:` header in the corresponding spec file.
- **Field shapes are wire-compatible.** `IssueClosed(string? Reason)` → `IssueCancelled(string? Reason)`; `IssueWorkCompleted(string WorkflowRunId)` → `IssueCompleted(string WorkflowRunId)` — confirmed in `IssueEvent.cs`.
- **Catalog claims hold.** `EventCatalog.ReverseDns` already defines `IssueCompleted`/`IssueCancelled` (127-128) plus the dead `IssueWorkCompleted` (130); `closed` was never catalogued — matches design D2.
- **Backfill target is correct.** Persisted `IssueEvents.Type` stores the reverse-DNS bus type (verified via `IssueQuerier` reading `row.Type == WorkCompletedType`), so the T-002 raw-SQL rewrite of the string ids is the right repair.

## Blocking Items

None.

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: `specs/issue-terminal-events/spec.md` requirement "The event serializer emits canonical reverse-DNS terminal ids through catalog constants" ends with "The persisted storage-facing type of a terminal event SHALL be the renamed variant's CLR type name (IssueCancelled / IssueCompleted)." The word "persisted" is mildly ambiguous because the actual persisted `IssueEvents.Type` column stores the reverse-DNS bus type, while the CLR name is what `IssueEventSerializer.Type()` (a dead-but-kept helper, documented in-code as "Storage-facing type") returns. The sentence is technically accurate for `Type()`'s contract and design D1 disambiguates the column vs. the CLR name explicitly, so no implementer can go wrong following design + tasks. No change needed now.
  SuggestedAction: Optionally reword to "the storage-facing CLR type returned by `IssueEventSerializer.Type()` SHALL be the renamed variant's name" to remove the "persisted" ambiguity. Pure prose, no behavioral impact.
  Status: follow-up

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-001 bundles the union rename, serializer/catalog routing, all six server consumer files, and ~9 spec files into one AFK task. This is not over-fine (the title is a functional capability, no micro-actions, no standalone test task); the atomicity is forced because the C# 14 `union` makes the variant sweep compile-checked — a partial rename cannot compile, so splitting would produce non-green intermediate states. Granularity is therefore correct, just large.
  SuggestedAction: None required. If an AFK run of T-001 struggles with scope, the only safe seam is server-consumers-vs-specs (still must land together to compile); do not split along the type-name rename boundary.
  Status: follow-up

<promise>PASS</promise>
