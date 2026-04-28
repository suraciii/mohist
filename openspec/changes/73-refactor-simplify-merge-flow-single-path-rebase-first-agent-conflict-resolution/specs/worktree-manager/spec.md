## ADDED Requirements

### Requirement: WorktreeManager 支持快速前进检测

WorktreeManager SHALL 提供 `canFastForward()` 方法，使用 `git merge-base --is-ancestor` 检测分支是否线性领先于 baseBranch。

#### Scenario: 分支可以快速前进

- **WHEN** 调用 `canFastForward(projectPath, projectName, issueNumber, baseBranch)`
- **AND** `mo/issue-{N}` 的 HEAD 是 `origin/<baseBranch>` 的 descendant（线性领先）
- **THEN** 返回 `true`

#### Scenario: 分支不可以快速前进

- **WHEN** 调用 `canFastForward(projectPath, projectName, issueNumber, baseBranch)`
- **AND** `mo/issue-{N}` 的 HEAD 不是 `origin/<baseBranch>` 的 descendant（有分叉）
- **THEN** 返回 `false`

#### Scenario: 分支不存在

- **WHEN** 调用 `canFastForward(projectPath, projectName, issueNumber, baseBranch)`
- **AND** worktree 或分支不存在
- **THEN** 返回 `false`

### Requirement: WorktreeManager 支持带冲突标记的 rebase

WorktreeManager 的 `rebaseOntoMaster()` SHALL 支持 `abortOnConflict` 选项。当 `abortOnConflict` 为 `false` 时，rebase 冲突不自动 abort，冲突标记留在 worktree 中供 agent 处理。

#### Scenario: rebase 成功无冲突

- **WHEN** 调用 `rebaseOntoMaster(projectPath, projectName, issueNumber, baseBranch, { abortOnConflict: false })`
- **AND** rebase 过程无冲突
- **THEN** 返回 `{ success: true, conflicts: [] }`

#### Scenario: rebase 冲突，abortOnConflict=false 保留标记

- **WHEN** 调用 `rebaseOntoMaster(..., { abortOnConflict: false })`
- **AND** rebase 遇到冲突
- **THEN** 不执行 `git rebase --abort`
- **AND** 冲突标记（`<<<<<<<`/`=======`/`>>>>>>>`）保留在 worktree 文件中
- **AND** 返回 `{ success: false, conflicts: ['file1.ts', 'file2.ts'] }`
- **AND** worktree 处于 rebase-in-progress 状态

#### Scenario: rebase 冲突，abortOnConflict=true（默认行为）abort

- **WHEN** 调用 `rebaseOntoMaster(..., { abortOnConflict: true })` 或不传选项
- **AND** rebase 遇到冲突
- **THEN** 执行 `git rebase --abort`
- **AND** 返回 `{ success: false, conflicts: ['file1.ts'] }`
- **AND** worktree 回到 rebase 前状态

### Requirement: WorktreeManager 支持 rebase continue

WorktreeManager SHALL 提供 `rebaseContinue()` 方法，在 agent 解决冲突标记后继续 rebase。

#### Scenario: 冲突已解决，rebase continue 成功

- **WHEN** 调用 `rebaseContinue(projectName, issueNumber)`
- **AND** worktree 中冲突标记已被解决（无未解决的冲突文件）
- **THEN** 执行 `git rebase --continue`（`GIT_EDITOR=true` 自动跳过 editor）
- **AND** 返回 `{ success: true, conflicts: [] }`

#### Scenario: 冲突未完全解决，rebase continue 仍有冲突

- **WHEN** 调用 `rebaseContinue(projectName, issueNumber)`
- **AND** worktree 中仍有未解决的冲突文件
- **THEN** 执行 `git rebase --continue` 失败
- **AND** 返回 `{ success: false, conflicts: ['remaining-conflict.ts'] }`
- **AND** rebase 仍处于 in-progress 状态

## REMOVED Requirements

### Requirement: mergeMasterInWorktree 反向合并

**Reason**: 反向 merge 产生 non-FF merge commits，违反 rebase-first 原则。所有合并冲突通过 rebase + agent resolution 处理。
**Migration**: 使用 `rebaseOntoMaster({ abortOnConflict: false })` + `rebaseContinue()` 替代反向合并。
