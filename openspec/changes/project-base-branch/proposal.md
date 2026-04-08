## Why

创建 worktree 时没有从确定性的起点创建分支，而是基于当前 HEAD。如果用户在 feature 分支上执行 `mo issue start`，新 worktree 就会基于那个 feature 分支，导致不可预测的起点。项目需要在数据库中记录主干分支（baseBranch），确保每个 issue 的 worktree 都从远程主干分支的最新状态创建。

## What Changes

- Project 模型新增 `baseBranch` 字段
- 数据库 schema 升级到 version 8，新增 `base_branch` 列
- `project create` 时自动检测主干分支（`git symbolic-ref refs/remotes/origin/HEAD`），支持 `--base-branch` 参数覆盖
- `WorktreeManager.create()` 从 `origin/<baseBranch>` 创建分支，而非未指定起点的 HEAD
- 已有项目通过 migration 自动检测并填充 `baseBranch`（回退到 "main"）

## Capabilities

### New Capabilities

_无_

### Modified Capabilities

- `project-management`: Project 创建时自动检测 baseBranch 并持久化；API 新增可选参数
- `worktree-manager`: 创建 worktree 时基于 `origin/<baseBranch>` 而非 HEAD

## Impact

- **数据库**: `projects` 表新增 `base_branch TEXT` 列，migration version 7 → 8
- **API**: `POST /api/projects` 新增可选 `baseBranch` 字段；`GET /api/projects/:id` 返回值新增 `baseBranch`
- **CLI**: `mo project create` 新增 `--base-branch` 选项
- **WorktreeManager**: `create()` 方法签名变更，需要接收 `baseBranch` 参数；内部执行 `git fetch origin` + `git worktree add -b <branch> origin/<baseBranch>`
