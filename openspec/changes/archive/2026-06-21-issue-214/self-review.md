# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` Impact line said the CLI subcommand goes "under the existing `runner` command group", but (a) the proposal's own What-Changes line already specified `mo runner show` (top-level), and (b) the existing `runner` group is actually `mo server runner` (service lifecycle: install/start/stop/restart/status/logs/uninstall). Design D5 deliberately resolves this in favor of a new top-level `mo runner` group, with rationale and alternatives; `tasks.json` T-004 follows the design. The Impact wording was the only stale artifact.
  Verification: Changed `proposal.md` line 30 to "New top-level `mo runner show` subcommand (separate from the existing `mo server runner` service-lifecycle group)". `grep` confirms proposal, design, and tasks now agree on a top-level `mo runner` group; no other artifact referenced the old wording.
  Status: resolved

## Blocking Items

_(none)_

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: Spec requirement "All active works are surfaced per runner, bounded by slots" includes the invariant "SHALL NOT exceed the runner's normalized maximum workflow slot count". This is already enforced by `RunnerGrain.PollAsync` (`if (ActiveWorkflowCount >= MaxWorkflowSlots) return null;`), so no new enforcement code is required, but the task acceptance criteria do not state the slot-bounding invariant explicitly.
  SuggestedAction: During T-001 implementation, add a grain-level assertion or test that `_works` size cannot exceed `MaxWorkflowSlots` (e.g., a multi-slot scenario test asserting the count stays within bounds). Low priority since the invariant pre-exists.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: completeness
  Evidence: Spec requirements "All active works are surfaced per runner, bounded by slots" and "Single runner can be queried by runner identifier" are covered by task descriptions/ACs but are not the primary `spec` anchor of any task (T-001 anchors "each-active-work-carries-full-work-level-context"; T-002 anchors "runner-list-endpoint-exposes-multi-item-active-work-per-runner"). Coverage exists, but the anchoring is implicit for those two requirements.
  SuggestedAction: Optionally add the secondary spec anchors to T-001/T-002 `spec` fields (e.g., comma-separated) so the traceability is explicit. Not blocking — descriptions and ACs already cover the behavior.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: alignment
  Evidence: Design Open Questions flag two UX decisions to confirm during implementation: (1) whether a registered-but-stale/offline runner returns 200 (with empty `ActiveWorks`) or 404 at the detail endpoint; (2) that `mo runner show` project selection matches `mo issue show`. Both are reasonable to defer to implementation, but should be resolved before T-002/T-004 close.
  SuggestedAction: Resolve both during T-002 / T-004 implementation and add explicit test coverage for the chosen behavior.
  Status: follow-up

## Summary

All four artifacts are mutually consistent and traceable to issue 214's six acceptance criteria and four non-goals:

- **Alignment**: every AC and Non-Goal maps to a spec requirement and at least one task. No issue requirement is missing or misinterpreted.
- **Completeness**: 9 spec requirements cover per-work context, multi-work surfacing, single-runner query, list enrichment, detail endpoint, Web detail page, list navigation, CLI show, and the read-only boundary. Edge cases (404, idle, missing issue ref, stale/offline) are addressed.
- **Consistency**: one new capability `runner-detail`, matching folder `specs/runner-detail/`; task `spec` anchors all resolve to real requirement headings; naming (`ActiveWorks`, `RunnerActiveWorkItem`, endpoint path, `mo runner show`) is uniform across artifacts. The one stale wording (proposal CLI group) is repaired (item-1).
- **Feasibility**: 4 tasks are complete feature slices (domain / API / Web / CLI), none over-split (no "define interface"/"register DI"/standalone test tasks), each bundles its own tests. Dependencies form a DAG: T-001 → T-002 → {T-003, T-004}.
- **Dependency completeness**: every non-first task has `dependsOn` pointing to an existing id with strictly lower priority; no cycles.

<promise>PASS</promise>
