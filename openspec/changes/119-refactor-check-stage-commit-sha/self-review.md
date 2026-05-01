## Self-Review: Issue #119

**Change**: Check stage 持久化检查结果 + commit SHA 快照 + 审批校验

### Completeness

| Requirement | Spec | Task | Status |
|---|---|---|---|
| Check Suite 数据模型 | `check-suite/spec.md` 数据模型 | T-001 | Covered |
| Check Suite snapshotSha 快照 | `check-suite/spec.md` 快照 | T-001, T-004 | Covered |
| Check Suite 检查结果持久化 | `check-suite/spec.md` 持久化 | T-004 | Covered |
| Check stage 循环重跑 | `check-suite/spec.md` 循环重跑 | T-004 | Covered |
| 移除 MergeReadyCheck | `check-suite/spec.md` 移除 | T-003 | Covered |
| Check Suite API 查询 | `check-suite/spec.md` API 查询 | T-005 | Covered |
| Check Suite 事件通知 | `check-suite/spec.md` 事件通知 | T-002, T-004 | Covered |
| Approve 端点 SHA 校验 | `http-api/spec.md` SHA 校验 | T-005 | Covered |
| Issue 详情包含 CheckSuite | `http-api/spec.md` 详情 | T-005 | Covered |
| Stage 描述和语义 (CHECK 循环) | `pipeline-model/spec.md` 语义 | T-004 (implicit) | Covered |
| CHECK 失败后回到 PLAN | `pipeline-model/spec.md` 回到 PLAN | T-004 AC includes escalateToStage=Plan | Covered |
| Web UI Check 面板 | `web-ui/spec.md` 面板 | T-006 | Covered |

All proposal capabilities (check-suite, http-api, pipeline-model, web-ui) have task coverage.

### Consistency

- Event naming: specs, design (D4), and tasks all use `check_update` (reused) + `check_suite_status_changed` (new). Consistent.
- Event payloads include `projectId` in all spec scenarios. Consistent with existing EventBus convention.
- `pipeline-model` spec has no direct `spec` reference in tasks but its requirements are covered by T-004 acceptance criteria (循环重跑 scenario + escalateToStage=Plan). Acceptable.
- Design D2 (implement in WorkflowController) aligns with T-004 output (`workflow-controller.ts`).

### Feasibility

- T-001 (types + migration + repo): Follows established patterns (CommentRepo, schema v17). Feasible.
- T-002 (EventBus): Single file change to EventMap type. Feasible.
- T-003 (remove MergeReadyCheck): Delete file + remove imports. Feasible.
- T-004 (loop logic): Most complex task — rewrites `runPipelineCheckStage()` with outer loop, CheckSuite persistence, SHA tracking. Auto-fix for ai-review reuses `spawnBuildTestFixAgent` pattern. Feasible but high complexity.
- T-005 (approve + API): Adds SHA validation to existing approve flow + new endpoint. Feasible.
- T-006 (frontend): New component following existing SSE patterns. Feasible.
- T-007 (tests): Tests for repo CRUD, SHA validation, loop behavior. Feasible.

### Dependency Graph

```
T-001 (p1) ──┬── T-002 (p2) ──┐
              ├── T-003 (p3) ──┼── T-004 (p4) ──┬── T-005 (p5) ──┬── T-006 (p6)
              └────────────────┘                 └─────────────────┼── T-007 (p7)
```

- Valid DAG: no cycles.
- Every non-first task has `dependsOn`.
- All `dependsOn` reference tasks with strictly lower priority.
- T-002 and T-003 can run in parallel (both depend only on T-001).

### Issues Found and Fixed

1. **Event name inconsistency (fixed)**: `check-suite/spec.md` and `web-ui/spec.md` initially referenced `check_state_changed` event, but design D4 decides to reuse existing `check_update`. Fixed both specs to use `check_update`.

2. **Missing `projectId` in event payloads (fixed)**: Spec event payloads were missing `projectId` which is required by EventBus convention. Added to both event payload definitions.

3. **Missing `escalateToStage=Plan` in T-004 (fixed)**: `pipeline-model/spec.md` requires CHECK failures escalate back to PLAN after max retries. T-004 acceptance criteria only said `success=false`. Added `escalateToStage=Plan` to AC and description.

4. **Web-ui spec reset scenario wording (fixed)**: Updated `check_state_changed` → `check_update` reference and clarified reset trigger wording.

### Remaining Notes

- `pipeline-model` spec has no direct task spec reference — its requirements are covered implicitly by T-004. This is acceptable because T-004 is the single implementation task for all CHECK stage behavior changes.
- T-004 is the highest-risk task (loop rewrite + persistence + SHA tracking). T-007 provides test coverage but runs after T-005. Consider manual verification of T-004 before proceeding.
- No open issues block implementation.
