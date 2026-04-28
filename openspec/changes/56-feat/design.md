## Context

当前 Ralph executor 执行 task 时，如果 agent 超时，`acp-session.ts` 直接 kill 子进程，worktree 中残留的未提交文件修改被忽略。重试时 agent 从空白状态重新开始。

**现有超时流程（数据丢失）：**
1. `ralph-executor.ts:455` — 调用 `runAcpSession()` 执行 task
2. `acp-session.ts:370` — `Promise.race` 检测到超时
3. `acp-session.ts:382` — `connection.cancel()` 取消 ACP session
4. `acp-session.ts:386` — `cleanup()` → `ensureKill()` 杀死子进程
5. `ralph-executor.ts:496` — `categorizeFailure()` 返回 `'timeout'`
6. `ralph-executor.ts:519` — `FAILURE_CATEGORY_CONFIGS.timeout.retryable === false`，直接 pause

**关键代码位置：**
- 超时触发点：`acp-session.ts:370-387`（single-session）和 `acp-session.ts:754-775`（multi-round）
- 失败分类：`ralph-executor.ts:29-34` — `timeout` 当前 `retryable: false, maxAttempts: 1`
- 重试 prompt 构建：`context-assembler.ts:192-194` — retry 时注入 failure context
- Worktree git 操作：`worktree-manager.ts:167-195` — `mergeBack()` 有 commit 逻辑可复用

## Goals / Non-Goals

**Goals:**
- 超时前保存 agent 已完成的代码修改为 WIP commit
- 有 WIP commit 的 timeout task 可以重试，agent 从断点继续
- WIP commit 在用户验收时被保留为最终实现

**Non-Goals:**
- 不修改 ACP 协议或 opencode 本身
- 不实现定期 checkpoint（只做超时前保存）
- 不修改 multi-round connection 模式（当前 Ralph 只用 single-session）
- 不修改 pipeline checkpoint 机制（task 级完成状态不变）

## Decisions

### D1: WIP commit 在 acp-session 超时路径中执行，而非 ralph-executor

WIP commit 必须在 agent 进程被 kill 之前执行。超时检测在 `acp-session.ts:370` 的 `Promise.race` 中触发，此时 agent 子进程可能仍在写入文件。在 `acp-session` 层执行 WIP commit 可以在 `cleanup()`/`ensureKill()` 之前完成保存。

**实现方式：** 在 `AcpSessionOptions` 中新增 `onBeforeKill?: (cwd: string) => Promise<void>` 回调。超时路径在 `cleanup()` 之前调用此回调。`ralph-executor` 传入回调函数，调用 `WorktreeManager.createWipCommit()`。

**Alternatives considered:**
- 在 ralph-executor 层检测超时后执行 WIP commit — 不可行，因为 `cleanup()` 已经 kill 了进程，文件可能处于不一致状态
- 使用 git fsck 监控实时变更 — 过于复杂，且需要 git hooks 支持
- 修改 opencode 添加 checkpoint 机制 — 涉及外部项目修改，不可控

### D2: `timeout` 失败类型改为条件重试

在 `FAILURE_CATEGORY_CONFIGS` 中不直接修改 `timeout` 的 `retryable`。改为在 `ralph-executor.ts` 的重试判断逻辑中增加条件：`timeout` 失败如果 WIP commit 存在，视为可重试（max 2 attempts）。

**实现方式：** `AcpSessionResult` 新增 `wipCommitted?: boolean` 字段。`categorizeFailure` 返回新类型 `'timeout_with_wip'`，配置为 `{ maxAttempts: 2, retryable: true }`。

**Alternatives considered:**
- 保持 `timeout` 不变，在重试循环中特殊处理 — 增加控制流复杂度，不如扩展分类表清晰
- 所有 timeout 都可重试 — 如果 agent 没有做任何修改，重试没有意义，浪费 token

### D3: 重试 prompt 通过 `BuildContextOptions` 扩展 WIP context

在 `BuildContextOptions` 中新增 `wipResumeContext?: string` 字段。`buildTaskContext()` 在组装 prompt 时，如果有 WIP context，注入 `[WIP Resume]` section。

**实现方式：** `ralph-executor` 在重试时调用 `WorktreeManager.findWipCommit()` 获取 diff 信息，构建 WIP resume context 字符串传入 `buildTaskContext()`。agent 因此知道哪些文件已修改，不会重新读取或重写。

**Alternatives considered:**
- 自动 squash WIP commit 到新 commit — 增加 git 操作复杂度，可能引入冲突
- 修改 `formatRetryContext()` — 该函数是通用重试逻辑，不应耦合 WIP 概念

### D4: WIP commit 使用独立 author 标识

WIP commit 的 author 设为 `mohist-wip <mohist@wip>`，与 agent 正常 commit 区分。`mergeBack()` 不需要修改，因为当前实现已经接受所有 commit（不做 squash）。

**Alternatives considered:**
- 使用默认 author — 不利于区分 WIP commit 和正常 commit，mergeBack 时难以做特殊处理
- 在 commit message 中加 tag 而非独立 author — `findWipCommit()` 用 message grep 可行，但 author 更可靠

## Risks / Trade-offs

**[WIP commit 时文件可能处于中间状态]** → Agent 可能在写入文件中途被超时中断。缓解：git add 会在 commit 时保存文件的瞬时状态；即使文件不完整，重试的 agent 会看到 diff 并继续完善。

**[WIP commit 增加超时处理延迟]** → git add + commit 通常 <2 秒，但在大型 worktree 中可能更慢。缓解：WIP commit 在 `onBeforeKill` 回调中执行，超时时间已到，不影响任务执行时间预算。如果 WIP commit 本身超时或失败，catch 错误并继续 kill 流程。

**[Agent 重试时可能重复已完成的修改]** → WIP resume context 包含 diff 信息，但 agent 可能忽略。缓解：prompt 中明确指示 "Do NOT re-read or re-implement the files listed above"。

**[多个 WIP commit 累积导致分支历史混乱]** → 每次 WIP commit 的 message 包含 attempt number，便于追踪。缓解：`mergeBack()` 保留所有 commit，用户可以通过 git log 看到 WIP 历史。

## Migration Plan

1. **Phase 1 — WorktreeManager 扩展**：添加 `createWipCommit()`、`findWipCommit()`、`getWipDiffSummary()` 方法。纯新增，无破坏性。
2. **Phase 2 — acp-session 回调**：添加 `onBeforeKill` 回调到 `AcpSessionOptions`。默认为 no-op，不影响现有调用方。
3. **Phase 3 — ralph-executor 条件重试**：添加 `timeout_with_wip` 分类，修改重试循环中传入 `onBeforeKill` 回调和 WIP resume context。行为变化仅限 timeout 场景。
4. **无回滚风险**：所有改动都是增量式的，不影响成功路径或非 timeout 失败路径的行为。

## Open Questions

- WIP commit 是否需要排除 `openspec/changes/` 和 `.opencode/` 目录？当前 `mergeBack()` 已排除这些目录。建议 WIP commit 也排除，保持一致性。
