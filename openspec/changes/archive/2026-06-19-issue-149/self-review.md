# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The `local-issue-store` delta spec's "Issues 表扩展" scenario stated a dedicated `labels` column (JSON object) is added at schema version 2. This contradicted design D3 ("no column, no migration; whole-state JSON") and the actual `IssueRow` code (`Infrastructure/Data/Issue/IssueRow.cs` has only `IssueId, State, ProjectId, Number, WorkflowRunId, Risk` — no `Labels` column; `IssueStore` serializes the whole aggregate into `State`). An implementing agent following the spec would have added a column + EF migration that the design and T-001 explicitly forbid. Rewrote the scenario as "Issue label storage" (labels persist as a JSON object inside the serialized aggregate state, no dedicated column, no schema migration) and added a "Legacy flat labels are discarded on load" scenario capturing the tolerant-deserialize edge case from design D3 / T-001 acceptance criteria.
  Verification: Re-read the edited `specs/local-issue-store/spec.md` — the requirement text and both scenarios now match design D3 and T-001 AC ("no EF migration or ModelSnapshot change"). Header audit confirms all scenarios still use exactly `####`; the `数据库扩展` requirement header is unchanged so the MODIFIED delta still targets the correct original requirement.
  Status: resolved

## Blocking Items

(none)

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: `GET /api/labels` is specified to return distinct label **keys** (consistent across `http-api`, `cli-interface`, and `local-issue-store` deltas), while the Web board filter needs `key=value` **pairs**. Design D7 resolves this by deriving value options client-side from loaded issues, so there is no contradiction — but the listing-vs-pairs question is flagged in design "Open Questions" for a future revisit (e.g. when board swim-laning / epic #8 child #2 lands).
  SuggestedAction: If a later issue needs server-side `key=value` discovery, revisit the `GET /api/labels` contract then; no action needed for this change.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: The issue's acceptance criterion "HTTP API 支持 key-value label 的 set / remove" is satisfied via full-replacement `PATCH` (set by including a key, remove by omitting it) plus domain-level `SetLabel`/`RemoveLabel`, per design D5. Granular `POST/DELETE /labels/:key` endpoints were intentionally deferred. This is a documented decision, not a gap, but is worth re-evaluating if concurrency or finer UX ever requires it.
  SuggestedAction: Track granular label endpoints as a candidate epic #8 follow-up; no action for this change.
  Status: follow-up

## Review Summary

- **Alignment**: Proposal, specs, design, and tasks all address issue #149 (labels `string[]` → single-value key-value). All 8 issue acceptance criteria trace to spec requirements and task acceptance criteria.
- **Completeness**: All 5 capabilities from the proposal (`issue-labels` new; `local-issue-store`, `http-api`, `web-ui`, `cli-interface` modified) have spec files; every spec is covered by a task (T-001 backend, T-002 web, T-003 cli). Edge cases covered: validation rejection, no-op event suppression, idempotent remove, tolerant legacy deserialization, board `key=value` matching.
- **Consistency**: Spec capability names match the proposal; task `spec` paths and requirement anchors all resolve to existing files/requirements (`Label operations are key-addressed`, `Issue Create/Edit label editor accepts key and value`, `Server API 扩展`). Design decisions D1–D7 align with the specs; the one storage-wording inconsistency (item-1) was repaired.
- **Feasibility**: Dependencies are acyclic and correct (T-002, T-003 → T-001). T-001 is deliberately one build-green task because the `string[]`→`Dictionary` type change is a compile break across all C# consumers (justified in T-001 notes); T-002/T-003 are independent parallelizable client slices. No tasks are over-fine (no standalone "define interface", "register DI", "move file", or test-only tasks); tests are embedded in each task's acceptance criteria.
- **Dependency completeness**: Every non-first task has `dependsOn`; all entries point to existing IDs with strictly lower priority (T-001 prio 1; T-002/T-003 prio 2 → T-001). No cycles.

<promise>PASS</promise>
