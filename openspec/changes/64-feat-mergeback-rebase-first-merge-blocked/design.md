## Context

当前 `MergeQueue.processItem()` 直接调用 `WorktreeManager.mergeBack()`，后者在 master worktree 内执行 `git merge <branch>`。当多个并行 issue 修改同一批文件时（如 #20/#34/#38 同时触碰 IssueCard.tsx），merge 阶段出现冲突，agent 无法在 master 上下文中解决 → blocked。

关键约束：
- `MergeQueue` 是串行的（`processing` flag 保证一次只处理一个 issue），所以不存在并发 merge race
- 但 issue A merge 后 master 前进，issue B 的分支基于旧 master，直接 merge 仍会冲突
- worktree 内拥有完整文件上下文，是执行 rebase 的理想位置
- server 重启后需恢复 merge 状态（已有 `recoverFromDB()`）

## Goals / Non-Goals

**Goals:**
- 将冲突检测从 master merge 阶段提前到 worktree 内 rebase 阶段
- rebase 成功后 merge 使用 `--ff-only`，保证零冲突
- blocked issue 在 master 有新 commit 时自动重试
- 文件重叠检测优化 merge 顺序，降低冲突概率

**Non-Goals:**
- 不实现自动冲突解决（rebase 冲突时仍 abort + 标记 conflict）
- 不修改 UI 显示（由 #40 覆盖）
- 不实现 agent 辅助冲突解决

## Decisions

### D1: rebase 在 worktree 内执行，不在主仓库执行

rebase 操作在 worktree 目录内执行（`git rebase origin/<baseBranch>` 在 worktreePath 下），而非在主仓库执行。原因是 worktree 拥有完整文件上下文，rebase 冲突时 git 可以正确检测文件内容。rebase 完成后，主仓库只需 `git merge --ff-only <branch>`，保证零冲突。

**Alternatives considered:**
- 在主仓库执行 rebase：会污染主仓库的 reflog，且与 stash/checkout 流程耦合更紧
- 使用 `git cherry-pick` 逐个 commit：复杂度高，且破坏 commit 历史

### D2: rebase 冲突时直接 abort，不尝试自动解决

rebase 遇到冲突时执行 `git rebase --abort`，将 mergeState 设为 `conflict`。不尝试自动解决（如 `git checkout --theirs`），因为错误的选择比等待更危险。

**Alternatives considered:**
- 自动选择 theirs/ours：语义不安全，可能静默丢弃代码
- Agent 辅助解决：agent 上下文不完整，成功率低，增加复杂性

### D3: auto-retry 使用 setInterval + master HEAD 比对

定时器每 5 分钟运行一次，通过 `git rev-parse HEAD` 获取当前 master HEAD，与上次尝试时记录的 HEAD 比对。有变化则重新入队。master HEAD 存储在 `MergeEntry.lastAttemptHead` 字段中。

**Alternatives considered:**
- 文件系统 watcher（fs.watch）：监听 .git 变化，复杂且不可靠
- webhook/commit-hook：需要 git server 支持，mohist 是本地工具

### D4: 文件重叠检测使用 `git diff --name-only`，结果缓存

在 `pickNext()` 中，对每个 pending issue 执行 `git diff --name-only <baseBranch>...<issueBranch>` 获取修改文件集合，存入 `MergeEntry.changedFiles` 缓存。选择下一个时，先按 FIFO 选候选，然后检查队列中是否有更早入队且文件重叠的 issue，有则优先处理。

**Alternatives considered:**
- 基于 commit message 的启发式分析：不准确
- 不排序、完全依赖 rebase：可行，但文件重叠检测可以提前避免不必要的 rebase 冲突

### D5: MergeEntry 扩展字段（retryCount, lastAttemptHead, changedFiles）

在 `MergeEntry` 接口上新增：
- `retryCount: number` — 自动重试计数，达到上限(5)后标记 blocked
- `lastAttemptHead?: string` — 上次尝试时的 master HEAD SHA
- `changedFiles?: string[]` — 缓存的修改文件列表

这些字段仅存于内存（不写入 DB），retryCount 在手动重试时重置为 0。server 重启后 retryCount 从 0 开始重新计数。

### D6: mergeBack() 简化为 fast-forward only

`WorktreeManager.mergeBack()` 移除现有的 `git merge <branch> --no-edit` 逻辑，改为 `git merge --ff-only <branch>`。rebase 已在 worktree 中完成，此处只做 fast-forward。如果 ff-only 失败（理论上不应发生），标记为 build-failed。

## Risks / Trade-offs

- **[rebase 改写 issue 分支历史]** → rebase 成功后 issue 分支的 commit SHA 变化，但 issue 分支是临时分支（`mo/issue-{N}`），不会影响其他协作者
- **[auto-retry 的 retryCount 仅在内存中]** → server 重启后重置为 0，可能多重试几次。可接受，因为重试本身无害（有新 commit 才触发）
- **[文件重叠检测增加 latency]** → `git diff --name-only` 在worktree 内执行，通常 < 100ms，且结果缓存只计算一次
- **[rebase --abort 后 worktree 状态]** → abort 后分支回到 rebase 前的状态，worktree 可继续使用，不影响后续重试

## Migration Plan

1. 扩展 `MergeState` 类型，添加 `rebasing` 和 `blocked`
2. `WorktreeManager` 新增 `rebaseOntoMaster()` 方法
3. `MergeQueue.processItem()` 重写为 rebase-first 流程
4. `MergeEntry` 扩展字段 + `pickNext()` 实现冲突感知排序
5. 新增 `startAutoRetry()` 定时器，在 server 启动时调用
6. `event-bus.ts` `EventMap` 添加 rebase 事件类型
7. 回滚策略：所有改动向后兼容——旧状态的 issue（pending/merging/conflict）在新代码下仍可正常处理，`recoverFromDB()` 已有覆盖

## Open Questions

- `blocked` 状态是否需要持久化到 DB？（当前设计中 retryCount 仅在内存，server 重启后重置。如果需要跨重启保持 blocked 状态，需 DB 持久化 retryCount）
