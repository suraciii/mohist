## Why

v5 E2E walkthrough 发现 3 个问题：

1. **Coder agent 不提交代码** [中等]：mohist 通过 ACP 发给 opencode agent 的 prompt 只包含 proposal/design/spec/task description，没有指示 agent 在完成 task 后 `git add + commit`。虽然 `commitBuildChanges()` 在所有 task 完成后做一次 bulk commit，但 task 间的新文件在 agent 看来是 untracked，可能影响后续 task 的依赖检测。根因在 `context-assembler.ts` 的 `buildTaskContext()` — 组装的 `fullPrompt` 缺少 git commit 指令。

2. **容器停止需 SIGKILL** [低]：walkthrough 用 `sleep infinity` 作为 PID 1，不响应 SIGTERM。但 `entrypoint.sh` 已有完整的 signal handler（trap TERM/INT + wait）。只需改 SKILL.md 用 entrypoint.sh 启动。

3. **Zombie 进程** [低]：同 #2 根因，`sleep infinity` 不回收子进程。entrypoint.sh 的 `trap CHLD` 已处理。

## What Changes

- 在 `context-assembler.ts` 的 `buildTaskContext()` 中，向 `fullPrompt` 追加 git commit 指令：告诉 coder agent 完成每个 task 后执行 `git add -A && git commit`
- 更新 mohist-walkthrough SKILL.md Step 3，用 entrypoint.sh 替代 `sleep infinity`

## Capabilities

### New Capabilities

- `coder-git-commit`: Coder agent 完成任务后自动 git commit，确保 task 间的文件依赖可见

### Modified Capabilities

（无）

## Impact

- `packages/cli/src/openspec/context-assembler.ts`: 在 fullPrompt 末尾追加 git commit 指令
- `.opencode/skills/mohist-walkthrough/SKILL.md`: 更新 Step 3 容器启动方式
