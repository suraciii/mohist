## Context

v5 E2E walkthrough 在容器 `mohist-wt-20260424-222607` 中发现 3 个问题。调查后确认了根因：

### 问题 1: Coder agent 不提交代码

**调用链追踪**：
```
context-assembler.ts: buildTaskContext()
  → fullPrompt = proposal + design + spec + learnings + task description
  → 没有 git commit 指令

ralph-executor.ts: _acpSessionRunner({ task: fullPrompt })
  → task 参数就是 fullPrompt

acp-session.ts: connection.prompt({ prompt: [{ type: 'text', text: task }] })
  → 直接发给 opencode agent
```

Agent 收到的 prompt 里没有任何 git commit 指令。虽然 `workflow-controller.ts:commitBuildChanges()` 会在所有 task 完成后做 bulk commit（filter 掉 openspec/changes/），但：
- task 间的新文件是 untracked，后续 task agent 可能读不到
- 如果 build 失败（v4 的 false-positive），`commitBuildChanges` 不会被调用，所有代码丢失

**修复方案**：在 `buildTaskContext()` 的 `fullPrompt` 末尾追加 git commit 指令。这样 agent 每次 task 完成后都会 commit，既解决 task 间依赖问题，也确保失败时已完成的 task 代码不丢失。

### 问题 2+3: 容器 SIGKILL + Zombie

walkthrough SKILL.md 用 `sleep infinity` 替代 entrypoint.sh 作为 PID 1：
- `sleep infinity` 不 trap SIGTERM → 需要 SIGKILL
- `sleep infinity` 不 wait 子进程 → zombie

但 `entrypoint.sh` 已有完整 handler：
- `trap 'shutdown=1; kill $stashed_pid' TERM INT`（line 14）
- `trap 'wait -n' CHLD`（line 15）

**修复方案**：改 SKILL.md 用 entrypoint.sh 启动容器。

## Goals / Non-Goals

**Goals:**
- Coder agent 每次 task 完成后自动 git commit
- Walkthrough 容器优雅停止（SIGTERM → exit 0）
- 消除 zombie 进程

**Non-Goals:**
- 不修改 `commitBuildChanges()`（它作为最终 safety net 仍然有用）
- 不修改 `entrypoint.sh`（它已正确实现）
- 不引入新外部依赖

## Decisions

### D1: 在 context-assembler 中追加 git commit 指令

**决策**: 在 `buildTaskContext()` 返回的 `fullPrompt` 末尾追加 git commit 指令。

**理由**: 这是最简单的方案，不影响现有代码结构。Agent 收到指令后在 task 完成时执行 `git add -A && git commit`。

**替代方案**:
- 在 ralph-executor 的每个 task 完成后调 `commitBuildChanges` → 需要传入 worktree 路径和 git 工具，增加 ralph executor 的职责
- 在 ACP session 的 system prompt 中加 → 不确定 ACP 协议是否支持 system prompt

### D2: Walkthrough 用 entrypoint.sh 替代 sleep infinity

**决策**: 改 SKILL.md Step 3 的 `podman run` 命令，不传 `sleep infinity`，让 ENTRYPOINT 生效。

**理由**: entrypoint.sh 已有完整的 signal handler 和子进程回收。

## Risks / Trade-offs

- **[Risk] Agent 可能不遵循 git commit 指令** → `commitBuildChanges` 作为 safety net 仍然存在，不会比现在更差
- **[Risk] Agent commit 消息不可控** → 使用 `--no-verify` 避免触发 hooks，commit message 由 agent 自己生成
- **[Risk] entrypoint.sh 的 wait loop 可能与 exec 操作冲突** → walkthrough 通过 `podman exec` 操作不影响主进程
