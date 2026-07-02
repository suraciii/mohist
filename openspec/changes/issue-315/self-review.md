# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `design.md` Risks (D3 mitigation) and `tasks.json` T-003 acceptance both claimed the caller applies the reverse-DNS outcome effects "in the exact same order (invalidate → setConflict → dispatch → toast)" / "preserving the previous execution order for each arm". This is factually inconsistent with the current code: the four arms apply effects in *different* orders — rebase-completed does `setRebaseConflict → dispatchRebaseEvent → invalidate` (invalidate last, no toast); rebase-conflict does `set → dispatch → toast → invalidate`; merge-success and merge-failure do `invalidate → toast`. An implementer following the stated order literally would diverge from the current code for 3 of 4 arms. The four sinks are in fact mutually independent (none awaits another; `setRebaseConflict` schedules an async React update a synchronously-dispatched rebase event cannot observe; `invalidateQueries` only schedules a refetch), so applying them in one canonical order is behaviorally safe — but the plan must not claim a per-arm order that the code never had. Edited `design.md` D3 mitigation and the T-003 acceptance criterion to state that the legacy per-arm order was non-uniform and the caller applies a single canonical order because the sinks are independent.
  Verification: Re-read `LiveTaskProvider.tsx:354-397` (the four arms) against the rewritten wording; the new statements accurately describe the independence of the four sinks and the non-uniformity of the legacy order. `tasks.json` re-validated as well-formed JSON with the dependency chain unchanged.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: completeness
  Evidence: T-004 moves `getCurrentIssueNumber` (LiveTaskProvider.tsx:216-219) out of `LiveTaskProvider.tsx` into `use-viewed-issue.ts`. `getCurrentIssueNumber` is part of the `__testing__` export surface (LiveTaskProvider.tsx:213) consumed by `LiveTaskProvider.test.ts`. T-002 establishes the "preserve `__testing__` bit-for-bit" invariant, but T-004 — the task that actually relocates `getCurrentIssueNumber` — did not restate it, leaving a gap where an implementer could drop the re-export. Added an acceptance criterion to T-004 requiring `__testing__` to still re-export `getCurrentIssueNumber` from the new module with the full key set unchanged.
  Verification: Confirmed `getCurrentIssueNumber` is in the `__testing__` object at LiveTaskProvider.tsx:213 and that T-002/T-006's `__testing__` key list matches the export. New T-004 criterion enumerates the exact key set. `tasks.json` re-validated as well-formed JSON.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: T-001 is a standalone `TEST`-type task ("Add test-first branch coverage…"). The review rubric flags isolated test tasks as a granularity smell. Here it is a justified, issue-mandated exception: the issue's explicit acceptance criterion is "测试先行：补齐 reverse-DNS outcome 与 lifecycle toast 分支测试后再抽代码", and design D2 (with its alternative-rejection rationale) explains the tests must characterize the *current in-file* code to act as a refactor safety net proving behavior preservation *across* the move — folding them into an extraction task would destroy that purpose. T-001's own notes document this reasoning. No merge performed.
  SuggestedAction: Leave T-001 as-is; the rationale is already recorded in design D2 and T-001 notes. If desired, the implementer may additionally add direct unit tests for the now-pure decider (already recommended by T-003 and design Open Question) once extraction lands.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: consistency
  Evidence: Task `spec` fields use simplified anchor slugs (e.g. `design.md#D3-reverse-dns-outcome-declarative`) that do not exactly match the rendered markdown heading slug (`#d3-reverse-dns-outcome-becomes-side-effect-free-declarative-result`). The `D1`–`D6` prefix unambiguously identifies the intended section, so the references are not ambiguous.
  SuggestedAction: Optional cosmetic normalization of the anchor text to match heading slugs exactly; not required for correctness.
  Status: follow-up

## Notes

- Verified against source (`packages/web/src/app/providers/LiveTaskProvider.tsx`, 601 lines): all design line references (`354-397` outcome handler, `227-259` runner-drop hook, `261-300` toast helpers, `302-337` timeline helpers, `410-430` history monkey-patch, `461-561` switch, `404` viewedIssueRef, `541`/`569` projectId closure, `17-34` compile-time guard) are accurate.
- Verified the Query-key contract: `invalidateApprovalWait` resolves to `['issues','metrics','approval-wait']` (`entities/issue/api/approval-wait.ts:30`), matching design/tasks. `['inbox', projectId]`, `['issues']`, `['issues','detail',id]`, `['agent-status']`, `['agent-activity']` all confirmed in `handleEvent`.
- Verified toast copy strings in code match T-001/T-004 assertions exactly (`Issue #N merged successfully`, `Rebase conflict on Issue #N`, `Merge failed for Issue #N`, `Issue #N needs approval`, `Issue #N encountered an error`, `Runner dropped`/`Runner reconnected`).
- Verified the test suite is 23 `it()` blocks; coverage gap (reverse-DNS outcome + lifecycle-toast arms untested) is real.
- No `openspec/changes/issue-315/specs/` directory exists; this is consistent with the proposal, which declares no New/Modified Capabilities (pure internal refactor — the existing `project-inbox-realtime` / `agent-session-visibility` specs describe behavior, not implementation layout, so no spec-level change is warranted).
- All 6 issue acceptance criteria trace to tasks; no issue requirement is missing or misinterpreted. Dependency graph is a clean linear chain (T-001→T-002→T-003→T-004→T-005 with T-005 also depending on T-003); no cycles; every `dependsOn` points to a lower-priority existing ID.

<promise>PASS</promise>
</content>
