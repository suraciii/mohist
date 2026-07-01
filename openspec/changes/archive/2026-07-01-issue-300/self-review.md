# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal's `Impact > Web` section asserted that the listed web files "consume the corrected values unchanged" and did not mention `IssueDetailPage.tsx:410`, even though that file bypasses the server capacity value (`isCapacityFull = activeAgents.length >= maxConcurrent`) and requires an actual code change. This created a proposal ↔ design ↔ tasks inconsistency: `design.md` D5 and `tasks.json` T-003 both implement the fix, but the proposal's Impact section omitted it, so the change was not traceable from the proposal.
  Verification: Added an explicit clause to `proposal.md` Impact-Web stating `IssueDetailPage.tsx:410` SHALL gate on server `capacity.active >= capacity.max` and that `activeAgents` retains visibility-only semantics. Re-read the proposal; it now aligns 1:1 with design D5 and T-003's acceptance criteria. No architectural change made; text-only traceability repair.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: completeness
  Evidence: `specs/agent-session-visibility/spec.md` is a MODIFIED requirement, but no task's `spec` field references it directly. The spec's capacity clause ("active-agents readout SHALL NOT be consumed as the source of capacity active-slot counts") restates the same invariant as `runner-capacity/spec.md#capacity-is-decoupled-from-agentsession-visibility`, which T-003 references and T-001 implements by removing the `activeAgents`→capacity consumption. The spec's remaining scenarios (generic `agent-launch` session appears, agent-attributed entry, workflow entries preserved) are explicit behavior-preservation clauses ("SHALL behave exactly as before this change") and require no standalone implementation task.
  SuggestedAction: No change required for correctness. If strict per-spec traceability is desired later, add a one-line note to T-001's `notes` field stating it also satisfies the `agent-session-visibility` capacity clause (schema-safe; do not overload the single-string `spec` field).
  Status: follow-up

## Notes

- **Alignment**: All five issue acceptance criteria are covered. AC#1/#2 (sidebar ↔ runner status ↔ CLI single source) → T-001 + `runner-capacity` spec. AC#3 (activeAgents = visibility only) → T-001/T-003 + `runner-capacity` decoupled requirement + `agent-session-visibility` spec. AC#4 (delete/rename misleading logic & tests) → T-001/T-002 rewrite `ApiContractSpecs`, `RuntimeEntrySpecs`, `AgentSessionSpecs`. AC#5 (divergence test: runner works > visible sessions) → explicit divergence acceptance criteria in both T-001 and T-002, plus scenarios in `runner-capacity` spec.
- **Scope of Dashboard pulse**: The issue AC list does not name `/agent/activity.summary.slots`, but unifying it is in-scope under the issue's "统一 runner capacity 口径" + "清理或改名容易误导的重复统计逻辑" — the `cards.Count(active)` / `runnerCount+1` heuristic is exactly the kind of misleading duplicate statistic the issue targets. T-002 + `dashboard-pulse` spec cover it. Not a misinterpretation.
- **Non-Goals respected**: No new capacity service/DTO (design D1/D2 add only a convenience accessor on the *existing* `RunnerStatusService`, explicitly "not a second aggregation model"); no AgentSession read-model rewrite; no scheduling/slot-allocation/workflow change; wire shape `{ active, max }` preserved (design D3).
- **Dependency completeness**: T-001 has empty `dependsOn` (first task, priority 1). T-002 → [T-001] (needs `GetCapacityAsync` accessor). T-003 → [T-001] (needs corrected `/agent/status.capacity.active`; does not depend on T-002 since IssueDetailPage consumes `/agent/status`, not `/agent/activity`). All `dependsOn` targets exist with strictly lower priority; no cycles.
- **Feasibility / granularity**: Three tasks, each a complete feature slice (sidebar route, pulse route, client gating) with tests embedded inline. No micro-tasks ("定义接口"/"提取类"/"注册DI"/"创建文件"), no standalone install/start/test tasks, no separate test-only tasks. T-001 is large but coherent — splitting it would create the over-fine anti-pattern; its acceptance criteria are detailed and self-contained.
- **Spec anchor verification**: All three task `spec` references resolve to existing `### Requirement:` headings (`runner-capacity` ×2, `dashboard-pulse` ×1).

<promise>PASS</promise>
