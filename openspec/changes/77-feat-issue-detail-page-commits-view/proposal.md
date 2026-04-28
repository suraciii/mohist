## Why

Issue 详情页的 Changed Files 区域只展示了最终的文件变更汇总，但用户无法看到代码是如何一步步构建的——即 worktree branch (`mo/issue-{N}`) 相对于 base branch 的提交历史。缺少 commit 级别的可见性，使得审查和理解变更演进过程变得困难。

## What Changes

- 在 Issue 详情页 Changed Files 区域新增 Commits 标签页，与 Files 视图并列切换
- 新增 `GET /api/issues/:number/commits` API，返回 worktree branch 相对 base branch 的提交列表（含 hash、message、author、date、文件变更统计）
- 新增 `GET /api/issues/:number/commits/:hash/diff` API，返回单个 commit 的 diff 内容
- 前端支持点击单个 commit 展开查看其 diff
- 无 worktree 或无 commits 时显示友好空状态提示

## Capabilities

### New Capabilities

- `issue-commits-api`: Issue commits 列表与单 commit diff 的 REST API
- `issue-commits-view`: Issue 详情页 Commits 标签页 UI 组件（列表 + 可展开 diff）

### Modified Capabilities

- `web-ui`: Issue 详情页 Changed Files 区域升级为 Files/Commits 双标签页切换布局
- `http-api`: 新增 commits 相关路由（`GET /:number/commits`、`GET /:number/commits/:hash/diff`）

## Impact

- **后端 API** (`packages/cli/src/api/issues.ts`): 新增 2 个路由 handler，复用已有的 worktree branch 查找模式（与 `/:number/diff` 路由一致）
- **Git 操作**: 使用 `git log <base>..<branch>` 和 `git show <hash>` 命令，无需新的 git 服务抽象
- **前端** (`packages/cli/web/src/components/IssueDetailPage.tsx`): Changed Files 区域重构为标签页切换布局，新增 commit 列表和 diff 展示
- **前端数据层** (`packages/cli/web/src/lib/api.ts`, `useQueries.ts`, `types.ts`): 新增 commits 相关类型、API 方法和 React Query hooks
