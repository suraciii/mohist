## Context

当前 `WorktreeManager.create()` 执行 `git worktree add <path> -b mo/issue-N` 时未指定分支起点，默认使用当前 HEAD。这导致 worktree 的起点取决于用户执行 `mo issue start` 时所在的分支，不确定且不可追溯。

当前 Project 模型只存储 `{id, name, path}`，没有 git 元数据。

## Goals / Non-Goals

**Goals:**

- Project 记录 `baseBranch`，作为创建 worktree 的确定性起点
- 创建 worktree 时基于 `origin/<baseBranch>`，确保从远程主干最新状态出发
- 已有项目通过 migration 自动填充 `baseBranch`

**Non-Goals:**

- 不存储远程仓库 URL（从 `git -C <path> remote get-url origin` 动态读取）
- 不支持多仓库（一个 Project 对应一个本地仓库，当前模型足够）
- 不改变 worktree 的目录结构或命名规范

## Decisions

### D1: baseBranch 检测策略

**选择**: 自动检测 + 手动覆盖

自动检测优先级：
1. `git symbolic-ref refs/remotes/origin/HEAD` → 解析出分支名（如 `main`）
2. 无 origin 或无 HEAD 引用 → 回退到 `"main"`

创建项目时通过 `--base-branch` 参数可覆盖自动检测结果。

**备选方案**: 只允许手动指定。否决，因为大多数项目遵循 `main`/`master` 约定，自动检测减少用户操作成本。

### D2: worktree 创建基于远程分支

**选择**: `git fetch origin` + `git worktree add -b mo/issue-N origin/<baseBranch>`

每次 start issue 时先 fetch，确保基于远程主干最新状态。

**备选方案**: 基于本地分支 `git worktree add -b mo/issue-N <baseBranch>`。否决，因为本地分支可能落后于远程，离线场景的便利不足以抵消staleness风险。

### D3: 已有项目 migration 策略

**选择**: migration 中对已有 project 自动检测 baseBranch 并回填

```sql
ALTER TABLE projects ADD COLUMN base_branch TEXT DEFAULT 'main';
```

检测逻辑复用 project create 的自动检测函数。检测失败时使用默认值 `"main"`。

### D4: Project 模型变更

`Project` 接口新增 `baseBranch: string`。`ProjectRow` 映射新增 `base_branch`。`ProjectRepo.update()` 支持更新该字段。

## Risks / Trade-offs

- **[fetch 增加延迟]** `git fetch origin` 在网络不佳时可能耗时数秒 → 可接受，start issue 不是高频操作；未来可加 `--no-fetch` 选项
- **[无 origin 的纯本地仓库]** `git symbolic-ref refs/remotes/origin/HEAD` 会失败 → 回退到默认值 `"main"`，worktree 创建时也回退到本地 HEAD
- **[baseBranch 改名后旧值残留]** 项目从 master 迁移到 main 后 DB 中还是 master → 提供 `mo project update --base-branch main` 手动更新
