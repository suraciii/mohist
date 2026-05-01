## Why

Check stage 的检查结果是临时的（ephemeral）——不持久化到 DB，不绑定 commit SHA。这导致三个问题：(1) auto-fix 产生新 commit 后，已通过的检查不会自动重跑，代码可能仍然 broken；(2) 用户审批时代码可能已变化（recovery 场景），但 approve 端点直接放行无校验；(3) MergeReadyCheck 总是 pass（informational only），不是代码质量检查却阻塞 Check 阶段语义，应该在合并时检查而非 Check 阶段。

## What Changes

- 持久化检查结果到 DB：每次检查的 status、output、ranAt 存入 check_suite 表，绑定 issue
- 引入 snapshotSha：Check stage 进入时记录 HEAD SHA，检查结果绑定该 SHA
- auto-fix 产生新 commit 后更新 snapshotSha 并重跑所有检查（循环，maxRetries=3）
- approve 端点增加 SHA 校验：HEAD != snapshotSha 时自动触发重跑检查，通过后才批准
- **BREAKING** 移除 MergeReadyCheck：merge-ready 是合并状态问题不是代码质量问题，当前合并流程已有 rebase 逻辑
- Check stage 执行流程从线性改为循环重跑：build-test fail → auto-fix → 新 commit → 从头重跑
- ai-review check 增加 auto-fix 能力（类似 build-test check 的现有 auto-fix）
- 前端 Check 面板展示检查项状态逐步更新

## Capabilities

### New Capabilities

- `check-suite` — Check Suite 数据模型与持久化：snapshotSha、suite status、各检查项的 CheckState（status/output/ranAt）

### Modified Capabilities

- `http-api` — approve 端点增加 SHA 校验逻辑：HEAD != snapshotSha 时返回提示并自动重跑检查
- `pipeline-model` — Check stage 语义变更：从线性执行改为循环重跑，移除 merge-ready 检查
- `web-ui` — Check 面板展示持久化的检查结果，逐步更新各检查项状态

## Impact

- `packages/cli/src/workflow/check-stage-runner.ts` — 循环执行逻辑 + SHA 快照记录
- `packages/cli/src/workflow/checks/merge-ready-check.ts` — 删除
- `packages/cli/src/workflow/checks/ai-review-check.ts` — 增加 auto-fix 能力
- `packages/cli/src/api/issues.ts` — approve 端点 SHA 校验
- `packages/cli/src/db/` — 新增 check_suite 表
- `packages/cli/src/types/index.ts` — CheckSuite / CheckState 类型
- 前端 Issue Detail 页面 Check 面板组件
- `packages/cli/src/services/agent-runner-service.ts` — CheckStageRunner 构造移除 MergeReadyCheck
