## Context

Check stage 当前有两套执行路径：(1) `WorkflowController.runPipelineCheckStage()` 直接执行检查（实际使用路径），(2) `CheckStageRunner` 通过 StageRunner 接口执行（未被 WorkflowController 调用）。两条路径都使用 `check_started`/`check_update` EventBus 事件通知前端，但检查结果仅存在于内存中的 `StageRunResult.output`，不持久化。

**当前数据流：**
```
WorkflowController.runPipelineCheckStage()
  → runBuildTestCheck()    (内含 auto-fix 循环)
  → runMergeReadyCheck()   (总是 pass)
  → runAiReviewCheck()     (无 auto-fix)
  → StageRunResult { output: { checks, overallResult } }  (ephemeral)
```

**Approve 流程：** `POST /api/issues/:number/approve` → 当 `approvalStage === Stage.Check` 时直接 enqueue merge queue，无 SHA 校验。

## Goals / Non-Goals

**Goals:**
- 持久化 CheckSuite 到 DB，绑定 snapshotSha
- Check stage 循环重跑（maxRetries=3），auto-fix 后从头重跑
- Approve 端点 SHA 校验，不匹配时自动重跑
- 移除 MergeReadyCheck（合并流程已有 rebase 逻辑）
- 前端实时展示检查状态

**Non-Goals:**
- 不重构 WorkflowController vs CheckStageRunner 的双路径问题（本 change 只改 WorkflowController 路径）
- 不改 CheckStageRunner（保留接口兼容，但本次不使用它）
- 不改 merge queue 的 rebase 逻辑
- 不新增前端组件框架（使用现有 SSE + React Query 模式）

## Decisions

### D1: 检查结果存储为 JSON 列而非独立表

`check_suites` 表的 `checks` 列存储 JSON 字符串，而非为每个检查项建独立行。

**理由：** 检查项固定为 build-test 和 ai-review，不会动态增减。单行 JSON 避免了多表 JOIN，读写简单，且与现有 `issues` 表中 `approval_state`（JSON 列）模式一致。

**Alternatives considered:**
- 独立 `check_results` 表每行一个检查项：过度工程化，当前只有 2 个检查项，不值得额外的 repo 和查询逻辑。

### D2: 在 WorkflowController 层实现循环重跑而非下沉到 Check 类

循环重跑逻辑放在 `WorkflowController.runPipelineCheckStage()` 中，不改变各个 Check 类的内部结构。CheckSuite 的创建、更新、SHA 读取都在 Controller 层完成。

**理由：** 当前实际执行路径就是 WorkflowController（不经过 CheckStageRunner），且循环需要协调多个检查（auto-fix 后从头重跑），这属于编排逻辑不是单个检查的职责。CheckSuiteRepo 通过 StageContext 传入。

**Alternatives considered:**
- 在 CheckStageRunner 中实现：需要先统一执行路径，超出本 change 范围。
- 在每个 Check 类中处理：build-test 已有内部 auto-fix 循环，再加外层循环会导致嵌套重试。

### D3: SHA 读取通过 git CLI

在 worktree 目录执行 `git rev-parse HEAD` 获取当前 SHA。不引入新依赖。

**理由：** 最简单直接，且 `WorktreeManager` 中已有类似 git 操作模式。

### D4: 复用现有 `check_update` 事件而非新增事件类型

继续使用 EventBus 中已有的 `check_started` 和 `check_update` 事件。在 `check_update` payload 中增加 `snapshotSha` 字段。新增 `check_suite_status_changed` 事件用于 suite 级别状态变更。

**理由：** 前端已订阅 `check_update` 事件，扩展字段比替换事件类型更平滑。

**Alternatives considered:**
- 全部使用新事件类型（`check_state_changed`、`check_suite_status_changed`）：需要前端同时监听新旧事件，迁移复杂。

### D5: Approve SHA 不匹配时返回 202 并后台重跑

SHA 不匹配时不拒绝请求，而是返回 HTTP 202（Accepted）并后台启动 Check stage 重跑。用户下次 approve 时检查应已通过。

**理由：** 拒绝请求（409）会让用户需要再次手动点击 approve。202 + 自动重跑对用户更友好。

### D6: CheckSuiteRepo 作为独立 repo

新建 `CheckSuiteRepo`（`db/check-suite-repo.ts`），遵循现有 repo 模式：构造函数接收 `DatabaseManager`，提供 `create`、`findActiveByIssueId`、`updateChecks`、`updateStatus`、`updateSnapshotSha` 等方法。

**理由：** 与现有 CommentRepo、WorkflowLogRepo 等模式一致。

## Risks / Trade-offs

**[循环重跑耗时过长]** → maxRetries=3 硬限制。每次循环包含 build-test + ai-review，最坏情况 3 次全流程。ai-review 本身耗时较长（含 ACP session），3 次循环可能需要 30+ 分钟。Mitigation：日志清晰记录每次循环的 attempt 和结果，便于监控。

**[auto-fix 未产生新 commit 导致无限循环]** → 每次循环后检查 HEAD SHA 是否变化。若 SHA 未变，视为重试失败，计入 maxRetries。

**[双路径维护成本]** → WorkflowController 和 CheckStageRunner 目前是两条独立路径。本 change 只改 WorkflowController 路径。CheckStageRunner 保留但实际不使用，后续可考虑统一。风险：未来维护者可能误改 CheckStageRunner。Mitigation：在代码注释中标注实际执行路径。

**[DB 迁移风险]** → 新增 `check_suites` 表是 additive migration，不影响现有数据。迁移函数 `migrateToVersion18` 仅 CREATE TABLE IF NOT EXISTS，可安全回滚（删除表）。

## Migration Plan

1. **Schema v18**：`migrations.ts` 新增 `migrateToVersion18()`，创建 `check_suites` 表和索引
2. **后端部署顺序**：先部署 DB 迁移 + API 层（向后兼容），再部署 WorkflowController 循环逻辑
3. **前端部署**：Check 面板组件，依赖 `check_update` 事件中的新字段
4. **删除 MergeReadyCheck**：与循环重跑逻辑同批部署，确保 CheckSuite.checks 中不再包含 merge-ready
5. **Rollback**：如需回滚，`check_suites` 表可安全 DROP，approve 端点降级为无 SHA 校验

## Open Questions

- ai-review 的 auto-fix 是否需要独立配置开关？当前 build-test 有 `autoFix` + `maxFixAttempts` 配置。建议本 change 中 ai-review auto-fix 默认开启，后续可通过 workflow.yaml 配置。
