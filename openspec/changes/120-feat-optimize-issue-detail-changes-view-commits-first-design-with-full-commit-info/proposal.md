## Why

Issue Detail 页面的 Changes 区域信息架构颠倒了优先级。用户监控 AI agent 工作时，首要需求是理解"agent 做了什么"（commit 时间线叙事），而不是审查代码 diff。当前设计照搬 GitHub PR 的 Files Changed 优先模式，且 Issue #91 的 noise commit filtering 会隐藏审计信息。需要重新设计为 commits-first，不做过滤，让 commit message 成为默认叙事线。

## What Changes

- **默认 tab 从 Files 改为 Commits**：commit message 是 agent 工作的叙事线，扫一遍 commit 列表就能理解 90% 的工作内容
- **移除 noise commit filtering**：所有 commit 都是审计轨迹，折叠 chore(tasks) 等会让用户丢失信息
- **commits API 增加文件列表**：解析 `git log --stat` 输出，在每个 commit 中返回 `files: string[]`，不展开 diff 就能看到改了哪些文件
- **commit 展开用 DiffViewer 替换 CommitDiffView**：替换无行号的简单 diff 显示，复用已有的 DiffViewer 组件（有行号、文件分块）
- **diff API 升级**：从 `--stat` symbol-counting 升级到 `--numstat` + 完整 diff，返回精确的 additions/deletions 统计和 per-file diff content
- **Files tab 接入 DiffViewer**：点击文件展开显示 inline diff，用 `--numstat` 精确统计替代不准确的 symbol counting
- **扩大 Changes 区域可见范围**：不仅 build/check/done 阶段可见，更早的阶段也能查看

## Capabilities

### New Capabilities

- `changes-commits-first`: Changes 区域 commits-first 设计 — Commits 为默认 tab，commit 行显示文件名列表，展开用 DiffViewer，不做 noise filtering

### Modified Capabilities

- `http-api`: `GET /api/issues/:number/commits` 响应增加 `files: string[]` 字段；`GET /api/issues/:number/diff` 从 `--stat` 升级到 `--numstat` + 完整 diff，返回精确统计和 per-file diff content
- `web-ui`: Issue Detail 页面 Changes 区域默认 tab 从 Files 改为 Commits，commit 展开使用 DiffViewer 组件，Files tab 也接入 DiffViewer
- `session-timeline-ui`: Changes 区域可见范围扩大到更多工作流阶段

## Impact

- **Backend** (`packages/cli/src/api/issues.ts`): `/commits` 路由增加 `--stat` 文件名解析；`/diff` 路由从 `--stat` 升级到 `--numstat` + `git diff`
- **Frontend** (`packages/cli/web/src/components/`): ChangesTab 默认切换为 Commits，CommitDiffView 替换为 DiffViewer，移除 noise filtering 逻辑
- **Data layer** (`packages/cli/web/src/lib/types.ts`, `api.ts`, `useQueries.ts`): DiffQuery 响应类型适配新 API 结构，CommitsQuery 响应增加 files 字段
- **Replaces Issue #91 direction**: Issue #91 的 Files Changed 默认 + noise filtering 设计被本 change 替代
