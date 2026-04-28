## Context

Issue 详情页在 Build/Review/Done 阶段显示 Changed Files 区域（`IssueDetailPage.tsx:295-310`），展示 `git diff <base>..<branch> --stat` 的汇总。当前只有最终文件级视图，无法看到代码是如何一步步构建的。

后端已有完整的 worktree branch 查找模式：`GET /:number/diff`（`issues.ts:1152-1226`）通过 `worktreeManager.exists()` + `execFileAsync('git', ...)` 执行 git 命令。新 API 可完全复用此模式。

前端数据层为 React Query + `api.ts` 封装，类型定义在 `types.ts`。

## Goals / Non-Goals

**Goals:**
- 在 Changed Files 区域增加 Files/Commits 标签页切换
- Commits 标签页展示 worktree branch 相对 base branch 的 commit 列表
- 每个 commit 可展开查看 diff（懒加载）
- 复用已有的 worktree branch 查找和 git 执行模式，不引入新抽象

**Non-Goals:**
- 不做 commit 级别的搜索/过滤
- 不做 diff 的 side-by-side 视图（仅 unified diff）
- 不新增 git 服务层抽象，直接在 API handler 中执行 git 命令

## Decisions

### D1: API handler 内联 git 命令，不抽象到 WorktreeManager

与现有 `/:number/diff` 路由一致，直接在 `issues.ts` 的 handler 中调用 `execFileAsync('git', ...)`。commits 列表和 diff 是纯展示逻辑，不需要跨模块复用。

**Alternatives considered:**
- 在 `WorktreeManager` 中添加 `getCommits()` / `getCommitDiff()` 方法——增加不必要的抽象层，且 WorktreeManager 目前只管理 worktree 生命周期，不负责展示查询。

### D2: Commits API 使用 `git log` 的 `--format` 和 `--stat` 组合

```
git log <baseBranch>..mo/issue-<N> --format=__FORMAT__ --stat
```

其中 format 为：`%h%x00%s%x00%an%x00%aI`（短 hash、message 首行、作者、ISO 日期），用 NUL 分隔字段。`--stat` 提供每个 commit 的文件变更统计（`X files changed, Y insertions(+), Z deletions(-)`）。

**Alternatives considered:**
- 使用 `git log --json`（Git 2.34+）——对 git 版本要求高，且输出过于冗余。
- 两次调用（一次 `git log --format` 获取列表，一次 `git log --stat` 获取统计）——合并为一次调用更高效。

### D3: 单 commit diff 使用 `git show --format="" --patch <hash>`

返回原始 unified diff 文本，前端直接渲染。不做 diff 解析或结构化输出。

**Alternatives considered:**
- 返回结构化 diff（按文件分组的 hunks）——增加后端复杂度，前端可按需迭代。

### D4: commit hash 归属验证使用 `git branch --contains <hash>`

`GET /:number/commits/:hash/diff` 需验证 hash 属于 `mo/issue-{N}` branch。使用 `git branch --contains <hash> --list mo/issue-<N>` 检查。若输出非空则 hash 属于该 branch。

**Alternatives considered:**
- 从 commits 列表 API 结果中缓存 hash 集合——增加状态管理复杂度，git 命令开销极小（<10ms）。

### D5: 前端 diff 展示为纯文本 + CSS 颜色标记

前端接收原始 diff 文本，按行渲染：`+` 开头行绿色背景，`-` 开头行红色背景，其余行灰色背景。不引入 diff 解析库。

**Alternatives considered:**
- 引入 `diff` 或 `diff2html` npm 包——增加依赖，当前展示需求简单，纯 CSS 足够。

### D6: Commits 列表和计数在同一 API 返回，不做双请求

`GET /:number/commits` 返回 `{ commits: [...] }`，前端通过 `commits.length` 得到计数。Commits 标签页标题 "Commits (N)" 在组件内计算。无需额外 API 获取计数。

## Risks / Trade-offs

- **[大量 commits 时 diff 加载慢]** → 每个 commit diff 按需懒加载（点击时请求），不预加载。单个 commit diff 通常较小（<100KB）。
- **[agent 持续产生新 commit 时数据可能过时]** → 依赖现有 SSE `stage_changed` 和 `agent_paused` 事件触发 React Query invalidation，保持与 diff 一致的刷新策略。
- **[worktree 刚创建或 rebase 中间状态]** → 与 `/:number/diff` 一致：无 worktree 返回空数组，rebase 中间状态由 git 命令自然处理。

## Migration Plan

纯增量变更，无破坏性改动。部署步骤：

1. 后端：在 `issues.ts` 中添加 2 个新路由（不影响现有路由）
2. 前端：修改 `IssueDetailPage.tsx` Changed Files 区域为标签页布局
3. 前端：在 `api.ts`、`types.ts`、`useQueries.ts` 中添加对应方法

无需数据库迁移或配置变更。回滚只需删除新增代码。

## Open Questions

无。
