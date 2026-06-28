# Self Review Report

## Result: PASS

## Repaired Items

(None — no safe repairs were needed. All artifacts are internally consistent and traceable.)

## Blocking Items

(None.)

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: consistency
  Evidence: T-003 covers two spec requirements (`CLI-provides-mo-runner-status-online-diagnostic-command` and `CLI-runner-service-status-preserves-the-service-lifecycle-status-verb`) because the rename and the new diagnostic must land atomically in `RunnerCommands.Build`. However, its `spec` field only references the first anchor. The task's title, description, and acceptance criteria (items 5–8) fully cover the service-status rename spec, so no requirement is orphaned — this is a traceability-pointer nicety only. Splitting into two tasks would be incorrect (atomic coupling), so the single-task design is the right call.
  SuggestedAction: Optionally add the second anchor (`#CLI-runner-service-status-preserves-the-service-lifecycle-status-verb`) to T-003's `spec` field or `notes` for explicit bidirectional traceability, pending confirmation that the tasks.json `spec` field accepts multiple anchors.
  Status: follow-up

## Review Summary

- **Alignment**: All three "What Changes" entries (system info, opencode models, runner online status) and the naming-collision resolution trace directly to issue #162 acceptance criteria. The endpoint-scope discrepancy (issue body describes `/api/opencode/models` and `/api/runner-status` as global; code verification shows both are project-scoped at `/api/projects/{projectRef}/...`) is correctly reconciled in the proposal Impact section, design Context, and spec project-resolution scenarios. No issue requirement is missing or misinterpreted.
- **Completeness**: Every requirement has a spec with concrete scenarios. All four spec requirements map to tasks (T-001, T-002, T-003). Edge cases covered: graceful server-down degradation, empty runner list, project-resolution failure, JSON raw-payload preservation, help disambiguation.
- **Consistency**: Specs live under the modified `cli-interface` capability as proposed. Tasks reference the correct spec file. Design Decisions 1–6 align with the spec scenarios. Naming is consistent across artifacts (`service-status` for the renamed lifecycle verb, `status` for the new diagnostic).
- **Feasibility**: All code references verified against the codebase (PrintRunnerShowAsync@285, RenderRunnerShow@341, FormatHeartbeat@201, SystemInfoResponse@87, route scopes, BuildSystemd status verb@124). Each task is a complete feature slice with embedded xUnit specs — none are pure refactor/rename/test-only tasks. T-003 correctly couples the rename with the new diagnostic (atomic `RunnerCommands.Build` modification).
- **Dependencies**: All three tasks are independent (`dependsOn: []`) with no cycles — they touch disjoint command groups/files. Priorities 1–3 reflect ordering preference only.

<promise>PASS</promise>
