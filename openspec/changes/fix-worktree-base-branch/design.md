## Context

mohist 的 worktree 创建流程存在 base branch 不一致问题，导致 agent 在错误的 git 历史上工作。问题在 E2E walkthrough 中暴露：

- `suraciii/mohist.git` remote 有 `master`（mohist 自身历史，307 commits）和 stale 的 `origin/main`（来自 fork 上游，29462 commits，已从远端删除但本地 ref 未清理）
- 两条历史无共同祖先
- 项目注册时 `detectBaseBranch()` 将 `base_branch` 存为 `'main'`
- worktree 从 `origin/main`（无关历史）创建
- `mo issue diff` 用独立的 `getDefaultBranch()` 解析为 `'master'`，与 worktree 无 merge base

当前有 3 个独立的 base branch 解析位置：
1. `src/git/detect-base-branch.ts` → 注册项目时用，fallback `'main'`
2. `src/cli/commands/issue.ts:11-30` → CLI diff 命令用，通过 `origin/HEAD`
3. `src/api/propose.ts:65` → 省略参数，默认 `'main'`

## Goals / Non-Goals

**Goals:**
- worktree 始终基于项目真实主干分支创建，不因 stale remote refs 或检测失败而出错
- 所有 base branch 消费者使用 DB 中存储的 `project.baseBranch` 作为唯一真相源
- 创建 worktree 后验证新分支与 base branch 有共同祖先
- `mo issue diff` 在 CLI 和 API 两端都能正确工作

**Non-Goals:**
- 不修复已存在的错误 worktree（用户可手动清理或重新创建）
- 不做项目 baseBranch 自动迁移（可通过 update API 手动修正）
- 不改变 worktree 的目录结构或命名方式

## Decisions

### D1: `detectBaseBranch` 改为多级探测 fallback

**选择**: 按优先级依次尝试：(1) `origin/HEAD` symbolic ref → (2) `origin/main` 存在 → (3) `origin/master` 存在 → (4) 当前 HEAD 分支名 → (5) 硬编码 `'main'`

**替代方案**: 仅依赖 `origin/HEAD`，失败则 `'main'`（当前行为）
**理由**: `origin/HEAD` 在 clone 后可能未设定（如 `git init` + `git remote add`），而 `origin/main` 和 `origin/master` 是最常见的默认分支名，探测它们比硬编码更可靠

### D2: 统一使用 `project.baseBranch` 消费 base branch

**选择**: CLI `mo issue diff` 从 API 获取项目的 `baseBranch`，而非本地调用 `getDefaultBranch()`。删除 `getDefaultBranch()` 函数。

**替代方案**: 保留 `getDefaultBranch()` 但让它也走多级探测
**理由**: DB 是唯一真相源。如果检测有误，用户可通过 API 修正。避免多套解析逻辑。

### D3: `smartFetch` 加 `--prune` 清理 stale refs

**选择**: 在 `smartFetch` 的 `git fetch origin` 命令中加入 `--prune` 参数

**替代方案**: 在 worktree 创建前单独调用 `git remote prune origin`
**理由**: `smartFetch` 已经是 worktree 创建的前置步骤，合并 prune 到 fetch 是最低成本的改动，且对所有 worktree 创建生效

### D4: worktree 创建后验证 merge base 存在

**选择**: 创建 worktree 后执行 `git merge-base <baseBranch> <issueBranch>`，如果无 merge base 则报错并自动清理已创建的 worktree

**替代方案**: 不验证，依赖用户发现问题
**理由**: 静默创建错误 worktree 代价很高（agent 在错误基础上工作），验证成本极低

### D5: `propose.ts` 传入 `project.baseBranch`

**选择**: `propose.ts:65` 调用 `worktreeManager.create()` 时传入 `project.baseBranch`

**理由**: 零风险修复，与 `issues.ts:331` 保持一致

## Risks / Trade-offs

- **[fetch --prune 可能误删其他工作流依赖的 remote refs]** → prune 只删除远端已不存在的 refs，不影响本地分支，风险可接受
- **[DB 中已有错误 baseBranch 值]** → 不做自动迁移，用户可通过 `mo project update` 或 API 手动修正。detectBaseBranch 修复后新项目不会遇到此问题
- **[merge base 验证在特殊仓库拓扑中可能误报]** → 对于确实有多个无关根的项目（如 git subtree），可能需要后续放宽。当前先严格验证
