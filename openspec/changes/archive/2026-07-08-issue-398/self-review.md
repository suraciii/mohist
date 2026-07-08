# Self Review Report

## Result: PASS

The plan artifacts (proposal, design, tasks, specs) are internally coherent and
cover every issue #398 acceptance criterion. Two safe repairs were applied
directly to `tasks.json`. Three follow-up items remain for human disposition;
none block implementation.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: completeness
  Evidence: `shared/ui/toast/RuntimeToastHost.tsx` was listed in the proposal
  Impact and explicitly named in the `status-surface-consistency` spec
  ("attention/toast surfaces (`AttentionHero`, `RuntimeToastHost`) SHALL render
  legibly in dark theme"), and the file currently authors 25 raw palette classes
  (`border-emerald-200`, `bg-amber-50`, `text-red-900`, `bg-slate-50`,
  `border-blue-200`, …) for its success/warning/error/default/info toasts.
  No task referenced it, leaving the toast surface — a core status-bearing
  surface named in the issue ACs — uncovered.
  Changed: appended `RuntimeToastHost.tsx` to T-003's routed-surfaces list
  (mapping its success/error/warning/info/default toasts to the
  success/danger/warning/info/muted families), added a dedicated acceptance
  criterion asserting the raw palette classes are gone and the toasts are
  dark-theme-legible, and added the file to T-003's output enumeration.
  Verification: `python3 -c "import json; json.load(open('openspec/changes/issue-398/tasks.json'))"`
  confirms the file is still valid JSON; the spec field, description, output,
  and acceptance criteria of T-003 now mention RuntimeToastHost consistently.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: consistency
  Evidence: T-003 implements the `theme-tokens` requirement "Log level and
  severity colors route through tokens" (it rewires `shared/lib/log-levels.ts`
  `LEVEL_COLORS`/`LEVEL_CHIP_COLORS` and the issue-event-timeline failure/
  attention markers), but its `spec` field did not reference
  `specs/theme-tokens/spec.md#log-level-and-severity-colors-route-through-tokens`,
  breaking the "tasks reference correct spec files" traceability invariant.
  Changed: added the missing spec anchor to T-003's `spec` field.
  Verification: JSON re-parses cleanly; the slug matches the requirement
  heading "Log level and severity colors route through tokens" in
  `specs/theme-tokens/spec.md`.
  Status: resolved

## Blocking Items

None. The one blocking-scope gap found (RuntimeToastHost with no owning task)
was safe to repair in place and has been resolved as item-1.

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: alignment
  Evidence: The proposal "What Changes" list contains the entry "Make the theme
  selector reachable beyond Settings only where the milestone covers surfaces
  (no new dependency, no new full-page redesign)." This entry does not trace
  back to any issue #398 acceptance criterion (the issue is silent on theme
  selector placement), has no corresponding design decision (D1–D8 do not
  mention it), no spec requirement, and no task. Either the line is orphaned
  scope creep, or it is a real requirement that the design/tasks forgot to
  pick up.
  SuggestedAction: Product owner clarifies intent. If the theme-selector
  reachability change is in scope, add a design decision + spec requirement +
  task for it (it is a real product change, not pure presentation). If it is
  not in scope, remove the line from `proposal.md` so the proposal stops
  over-promising. Not repaired here because both options are product/architectural
  changes that the repair policy forbids during self-review.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: completeness
  Evidence: The proposal Impact list names `shared/ui/ModelSelect.tsx` and
  "markdown-reader composites" as affected code, and both currently contain raw
  Tailwind palette classes (`ModelSelect.tsx` has `border-blue-500 bg-blue-500`,
  `bg-blue-50 text-blue-700`, `hover:bg-red-50 hover:text-red-500`; the
  markdown-reader has `bg-gray-100`, `border-gray-200`, `bg-amber-50`, etc.).
  Neither is covered by a task, and neither is a production-state surface in
  the sense the issue ACs enumerate (issue health, workflow stage, approval,
  runner state). `ModelSelect` selection highlighting and markdown body
  rendering are closer to interactive/content concerns than to status.
  SuggestedAction: Decide whether these are genuinely in scope. If not, trim
  the proposal Impact list to match. If they are (e.g. the `ModelSelect`
  delete affordance should use the destructive variant), add scoped
  acceptance criteria to T-005 (action buttons) or a follow-up issue. Not
  repaired here because the scope call is a product judgment, not a safe
  mechanical edit.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: consistency
  Evidence: The `theme-tokens` requirement "Success/warning/info/danger tokens
  are the single source of truth for status color" is architecturally satisfied
  by T-002 (the shared status-presentation layer becomes that single source)
  but no task's `spec` field references it. Unlike item-2 this is a meta/
  architectural requirement rather than a discrete deliverable, so the
  traceability gap is lower-impact.
  SuggestedAction: Optionally add the anchor
  `specs/theme-tokens/spec.md#successwarninginfodanger-tokens-are-the-single-source-of-truth-for-status-color`
  to T-002's `spec` field for full bidirectional traceability. Left as
  follow-up because the requirement has no separate verification beyond what
  T-002/T-003 already assert.
  Status: follow-up

## Notes on Verified Criteria

- **Alignment**: All five issue #398 acceptance criteria (cross-surface status
  consistency, dark-mode correctness, status-color meaning reservation, action
  consistency, product-term preservation) are traced to capabilities → specs →
  tasks. The only orphaned proposal line is item-3, which over-promises rather
  than under-covers.
- **Completeness**: Every spec requirement has at least one implementing task;
  every task traces to at least one spec anchor. RuntimeToastHost (the one
  in-scope surface missing from tasks) is repaired in item-1.
- **Consistency**: Capabilities, spec folders, spec headings, and task `spec`
  anchors use matching slugs. Design decisions D1–D8 map 1:1 to the specs and
  tasks (D1/D2→T-002, D3/D5→T-001, D4/D8→T-003, D6→T-004, D7→T-005).
- **Feasibility**: No task title is a pure mechanical action ("定义接口",
  "提取类", "注册DI", "创建文件", "rename", "move"). No task is pure code
  movement. No separate install/start/stop/uninstall task. No standalone
  "add tests" task — every task embeds its own verification criteria.
- **Dependency completeness**: T-001 (pr1, no deps) → T-002 (pr2) → T-003 (pr3);
  T-004 (pr4) and T-005 (pr5) branch from T-001. All `dependsOn` entries point
  to existing IDs with strictly lower priority. No cycles. T-004 correctly
  depends only on T-001 (it consumes the raw semantic token utilities, not
  `statusTreatment`); its coordination note with T-003 over kanban files is
  documented and touches disjoint files (`model/stage-colors.ts` vs
  `IssueCard.tsx`).

<promise>PASS</promise>
