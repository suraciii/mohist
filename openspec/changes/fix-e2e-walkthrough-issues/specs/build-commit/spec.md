## ADDED Requirements

### Requirement: Build 阶段完成后自动 commit 代码变更

Workflow controller SHALL 在 build 阶段的所有任务执行完成后，检查 worktree 中是否有未提交的代码变更。如果有，执行 `git add .` + `git commit` 将变更提交到 worktree 的 git 仓库。

#### Scenario: Build 完成后有代码变更
- **WHEN** build 阶段所有任务执行完成
- **AND** worktree 中存在 modified 或 untracked 文件（不含 openspec/changes/ 目录下的 openspec 产物）
- **THEN** workflow controller SHALL 执行 `git add . && git commit -m "build(issue-<number>): <issue title>"` 到 worktree
- **AND** commit 成功后继续 pipeline 下一阶段

#### Scenario: Build 完成后无代码变更
- **WHEN** build 阶段所有任务执行完成
- **AND** worktree 中无 modified 或 untracked 文件
- **THEN** workflow controller SHALL 跳过 commit 步骤
- **AND** 记录 info 日志 "No changes to commit after build stage"

#### Scenario: Git commit 失败
- **WHEN** build 阶段完成后 `git commit` 执行失败
- **THEN** workflow controller SHALL 记录警告日志（含错误信息）
- **AND** 不阻塞 pipeline 继续（commit 失败不是致命错误）

#### Scenario: Git commit 排除 openspec 产物
- **WHEN** worktree 中有 openspec/changes/ 目录下的文件变更
- **AND** 这些变更是 openspec 产物（proposal.md、design.md 等）
- **THEN** commit 不应包含这些文件（通过 .gitignore 或显式排除）
- **AND** 只提交代码文件（src/、tests/ 等）
