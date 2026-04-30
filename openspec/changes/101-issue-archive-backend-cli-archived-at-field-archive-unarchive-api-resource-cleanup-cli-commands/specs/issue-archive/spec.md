## ADDED Requirements

### Requirement: Issue 归档状态标记

系统 SHALL 支持通过 `archived_at` 字段标记 Issue 为已归档。`archived_at` 为 `TEXT DEFAULT NULL`，非 NULL 时表示归档时间戳。

#### Scenario: 归档单个 Issue

- **WHEN** 用户对 stage=done 且 status=completed 的 issue 执行归档
- **THEN** 系统设置 `archived_at` 为当前 ISO 时间戳
- **AND** 系统更新 `updated_at`

#### Scenario: 取消归档 Issue

- **WHEN** 用户对已归档 issue 执行 unarchive
- **THEN** 系统清除 `archived_at`（设为 NULL）
- **AND** issue 保持原来的 stage 和 status 不变
- **AND** 系统更新 `updated_at`

#### Scenario: 归档运行中的 Issue 被拒绝

- **WHEN** 用户尝试归档一个有活跃 agent session 的 issue（status=active 且存在运行中的 agent）
- **THEN** 系统返回错误 "Cannot archive: issue has a running agent. Force-stop it first."
- **AND** `archived_at` 不变

#### Scenario: 归档 Active 但未完成的 Issue

- **WHEN** 用户尝试归档 status=active 且 stage 不是 done 的 issue
- **THEN** 系统允许归档但返回警告 "Warning: Issue #N is not completed (stage: {stage}). Archived anyway."

### Requirement: 归档时清理关联资源

系统 SHALL 在归档时清理 worktree 和 openspec changes，除非指定 `cleanup=false`。

#### Scenario: 归档时移除 worktree

- **WHEN** issue 归档执行（cleanup=true）
- **AND** issue 对应的 worktree 目录 `~/.mohist/projects/{projectName}/worktrees/issue-{N}/` 存在
- **THEN** 系统执行 `git worktree remove` 移除 worktree
- **AND** 系统执行 `git branch -d mo/issue-{N}` 删除对应分支

#### Scenario: 归档时 worktree 不存在

- **WHEN** issue 归档执行（cleanup=true）
- **AND** issue 对应的 worktree 目录不存在
- **THEN** 系统跳过 worktree 清理，不报错

#### Scenario: 归档时迁移 openspec changes

- **WHEN** issue 归档执行（cleanup=true）
- **AND** `openspec/changes/{N}-{slug}/` 目录存在
- **THEN** 系统将目录移动到 `openspec/changes/archive/YYYY-MM-DD-{slug}/`
- **AND** 日期使用归档执行日期

#### Scenario: 归档时 openspec changes 不存在

- **WHEN** issue 归档执行（cleanup=true）
- **AND** `openspec/changes/{N}-{slug}/` 目录不存在
- **THEN** 系统跳过 openspec 归档，不报错

#### Scenario: 归档时清理 pipeline checkpoint

- **WHEN** issue 归档执行（cleanup=true）
- **AND** issue 存在残留的 pipeline checkpoint 数据
- **THEN** 系统清理该 issue 的 checkpoint

#### Scenario: 归档跳过资源清理

- **WHEN** 用户执行归档时指定 `cleanup=false`（--no-cleanup）
- **THEN** 系统仅设置 `archived_at`
- **AND** 不执行 worktree 移除、openspec 迁移或 checkpoint 清理

### Requirement: 批量归档已完成的 Issue

系统 SHALL 支持一键归档当前项目所有 stage=done 的 issue。

#### Scenario: 批量归档所有已完成 issue

- **WHEN** 用户执行批量归档（archive --all-completed）
- **THEN** 系统查找当前项目中所有 stage=done 且 archived_at IS NULL 的 issue
- **AND** 逐一执行归档（含资源清理）
- **AND** 返回归档数量摘要 "Archived {N} issues."

#### Scenario: 没有可归档的 issue

- **WHEN** 用户执行批量归档
- **AND** 没有符合条件的 issue（所有 done 的 issue 已归档，或没有 done 的 issue）
- **THEN** 系统返回 "No completed issues to archive."

### Requirement: Unarchive 恢复 openspec 目录

系统 SHALL 支持在 unarchive 时恢复 openspec changes 目录。

#### Scenario: Unarchive 恢复 openspec

- **WHEN** 用户执行 unarchive
- **AND** `openspec/changes/archive/` 下存在该 issue 的归档目录（按 slug 匹配）
- **THEN** 系统将目录移回 `openspec/changes/{N}-{slug}/`

#### Scenario: Unarchive 时归档目录不存在

- **WHEN** 用户执行 unarchive
- **AND** `openspec/changes/archive/` 下不存在该 issue 的归档目录
- **THEN** 系统跳过 openspec 恢复，不报错
