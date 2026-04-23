## Why

Worktree 基于错误的 git 历史创建：项目注册时 `detectBaseBranch()` fallback 到硬编码 `'main'`，而仓库实际主干分支是 `master`。当 remote 上存在 stale 的 `origin/main`（来自 fork 上游、已删除但本地 ref 未清理），worktree 会基于这条无关历史创建，导致 `mo issue diff` 报 "no merge base" 错误。同时，CLI diff 命令和 worktree 创建使用两套独立的 base branch 解析逻辑，产生不一致。

## What Changes

### 立即修复 (P0)
- **修复 `propose.ts`**：调用 `worktreeManager.create()` 时传入 `project.baseBranch`，而非省略参数使用默认值（一行代码，零风险）

### 根本修复 (P1-P3)
- **修复 `detectBaseBranch()`**：当 `origin/HEAD` 检测失败时，不再硬编码 fallback 到 `'main'`，改为按优先级依次尝试 `main` → `master` → 当前 HEAD 分支名
- **同步更新 migration**：`src/db/migrations.ts` 中的 `detectBaseBranchSync()` 也使用相同的多级探测逻辑
- **统一 base branch 解析**：CLI `mo issue diff` 命令从 API 获取 `baseBranch`，而非独立调用 `getDefaultBranch()`
- **API 响应增强**：Issue detail 端点返回 `baseBranch` 字段，供 CLI 使用

### 防御性措施 (P2)
- **worktree 创建前 prune stale refs**：在 `smartFetch` 中加入 `--prune`，避免基于已删除的远程分支创建 worktree
- **worktree 创建后验证 merge base**：创建 worktree 后检查新分支与 base branch 是否有共同祖先

## Capabilities

### New Capabilities

- `base-branch-resolution`: 统一的 base branch 解析策略，包含多级 fallback 和验证逻辑

### Modified Capabilities

- `worktree-manager`: 增加 prune 和 merge base 验证；创建时确保 base branch 有效
- `project-management`: `detectBaseBranch` fallback 策略从硬编码 `'main'` 改为多级探测

## Impact

### 立即生效 (T-000)
- `src/api/propose.ts`：传入 `project.baseBranch`（一行代码）

### 根本修复 (T-001 ~ T-003)
- `src/git/detect-base-branch.ts`：重写 fallback 逻辑
- `src/db/migrations.ts`：`detectBaseBranchSync()` 同步更新多级探测
- `src/git/worktree-manager.ts`：smartFetch 加 `--prune`，create 后验证 merge base
- `src/cli/commands/issue.ts`：`mo issue diff` 改用 API 返回的 `baseBranch`
- `src/api/issues.ts`：issue detail 响应添加 `baseBranch` 字段

### 数据影响
- 现有项目的 DB `base_branch` 值可能需要修正（可通过项目 update API 手动修正）
- 新注册项目将自动使用正确的 base branch
