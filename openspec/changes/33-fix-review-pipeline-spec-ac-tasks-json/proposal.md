## Why

Review pipeline 的三层防线（spec 合规、AC 验证、tasks.json 状态同步）全部失效：Review Agent 不读 specs 无法发现实现偏差，Acceptance Criteria 只渲染不验证，tasks.json 最后一个 task 的 passes=true 更新永远被 mergeBack 排除。Issue #30 的颜色值错误和月份格式遗漏直接证明了这些防线缺失的后果。

## What Changes

- `buildReviewerPrompt` 注入 changeDir 的 specs/ 目录内容和 tasks.json，使 Review Agent 具备 spec 合规审查能力
- `review.md` 新增 "Spec Compliance" 审查维度，逐条对照 acceptance criteria 验证精确值
- `review-self-check.md` 增加 spec 合规验证项，检查 report 是否逐条引用了 AC
- 修复 tasks.json 同步丢失：ralph 更新 tasks.json 后在 worktree 内追加 commit，并从 mergeBack 排除规则中移除 `openspec/changes/` 路径
- `packages/cli/web/` 提取 utility 函数并增加精确值断言测试

## Capabilities

### New Capabilities

- `web-unit-tests` — web/ 目录的单元测试基础设施（vitest 配置 + 精确值断言）

### Modified Capabilities

- `ralph-task-execution` — tasks.json 更新后追加 commit 确保同步
- `agent-spec-review` — Review Agent prompt 注入 specs 上下文；review.md 新增 Spec Compliance 维度
- `worktree-manager` — mergeBack 排除规则调整，确保 tasks.json 更新不被丢弃
- `agent-runtime` — review-self-check prompt 增加 spec 合规内容验证

## Impact

- `packages/cli/src/agents/artifact-prompt.ts` — buildReviewerPrompt 增加 spec 上下文注入
- `packages/cli/src/agents/prompts/review.md` — 新增 Spec Compliance 维度
- `packages/cli/src/agents/prompts/artifacts/review-self-check.md` — 增加 spec 合规验证项
- `packages/cli/src/openspec/ralph-executor.ts` — tasks.json commit
- `packages/cli/src/git/worktree-manager.ts` — mergeBack 排除规则修改
- `packages/cli/web/` — 提取 utility 函数到 src/lib/，新增测试文件
