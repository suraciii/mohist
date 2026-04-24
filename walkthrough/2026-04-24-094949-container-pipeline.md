# E2E Walkthrough: Container-based Pipeline

**日期**: 2026-04-24 09:49
**目标**: 验证更新后的容器化 walkthrough skill 是否能走通
**状态**: 阻塞 — 容器缺少 opencode，无法完成完整 pipeline
**容器**: mohist-wt-095300 (已清理)

---

## 进度记录

### Step 1: Build Container Image ✅
- 基础镜像 `mohist-test` 已存在 (847 MB)
- `podman build -t mohist-walkthrough -f test/agentic/shared/Containerfile test/agentic/` 成功
- Containerfile 仅 `FROM mohist-test`，无额外层

### Step 2: Run Container ❌ → ⚠️
- **问题**: `podman run -d -p 3456:3456` 失败，端口被主机 mo-server 占用
- **解决**: 去掉端口映射，改用 `podman exec` 在容器内操作
- **问题**: 容器立即退出 (exit 0) — entrypoint 启动 server 后 `exec bash` 在无 TTY 时立即退出
- **解决**: 传 `sleep infinity` 作为 CMD 保持容器存活

### Step 3: Server Health ✅
- `curl http://localhost:3456/api/health` → `{"status":"ok"}`

### Step 4: Project Setup ✅
- `mo project create walkthrough-test` 成功
- 发现: 基础镜像有来自之前测试的 `test-project` 残留数据和 mohist.db

### Step 5: Create Issue ✅
- `mo issue create "E2E walkthrough test"` → #1 创建成功，stage=draft

### Step 6: Start Issue #1 ❌
- `mo issue start 1` → 失败: `Failed to create worktree: git worktree add ... fatal: not a valid object name: 'HEAD'`
- **根因**: 空 git repo（无 commit），git worktree add 需要 HEAD 指向有效 commit
- **副作用**: stage 从 draft 变成了 plan，即使操作失败（状态不一致 bug）

### Step 7: Start Issue #2 (with initial commit) ⚠️
- 添加初始 commit 后创建 issue #2 并 start
- `mo issue start 2` → 成功，但 status 立即变为 blocked
- **根因**: 容器无 opencode 二进制，agent 无法 spawn
- pipeline 在此阻塞，无法继续

### Step 8: Diagnostics
- `/api/agent/status` → 空（无 agent 运行）
- `~/.mohist/logs/` → 空（无日志文件）
- `ps aux` → 仅 mo-server 和 sleep infinity
- worktree `issue-2` 已创建但只有 .git 文件（无实际内容）

---

## 发现的问题

### 问题 #1: 容器无 keep-alive 机制 [严重]
- **现象**: `podman run -d mohist-walkthrough` 后容器立即退出
- **根因**: entrypoint.sh 启动 mo-server 后执行 `exec "$@"`，默认 CMD `bash` 在无 TTY 时退出
- **证据**: `podman logs` 显示 server 启动成功后无输出，exit code 0
- **建议**: entrypoint.sh 应改为 `exec mo-server`（前台运行），或默认 CMD 改为 `sleep infinity`

### 问题 #2: 空仓库 worktree 创建失败 [严重]
- **现象**: `mo issue start` 在空 git repo 上失败: `fatal: not a valid object name: 'HEAD'`
- **根因**: `WorktreeManager.create()` 的 `branchExists()` 检查通过后，`git worktree add -b mo/issue-1 master` 在空 repo 上失败（master 分支无 commit）
- **证据**: `packages/cli/src/git/worktree-manager.ts:132-142`
- **建议**: 在 `mo project create` 或 `mo issue start` 中验证 repo 至少有一个 commit；或在 `branchExists` 中增加对空分支的检查

### 问题 #3: 失败操作导致状态不一致 [严重]
- **现象**: `mo issue start 1` 失败（worktree 创建错误），但 stage 从 draft 变为 plan
- **根因**: `packages/cli/src/api/issues.ts:335` `transitionToStage` 在 worktree 创建之后执行。但 catch 块 (line 384-396) 的 `stageTransitioned` 标志在 worktree 抛异常时为 false（line 336 未执行），不会触发 rollback。实际 stage 变化可能是 agent runner 内部触发的。
- **证据**: issue #1 在 start 失败后 `mo issue show 1` 显示 stage=plan
- **建议**: 将 `transitionToStage` 移到 worktree 创建成功之后，或在 worktree 失败时确保 rollback

### 问题 #4: 容器缺少 opencode [严重]
- **现象**: `mo issue start` 成功后 issue 立即变为 blocked，无 agent 进程
- **根因**: 容器基于 Layer A（仅 mohist 核心），无 opencode 二进制。AgentRunner 尝试 spawn opencode 失败
- **证据**: `which opencode` → not found；issue status=blocked
- **建议**: 为 walkthrough 创建 Layer B 镜像（FROM mohist-test + COPY opencode），或在 SKILL.md 中说明完整 pipeline 需要 Layer B

### 问题 #5: 容器缺 jq [低]
- **现象**: `jq: command not found`
- **根因**: 基础镜像未安装 jq
- **证据**: check-status.sh 和诊断命令依赖 jq
- **建议**: 在基础镜像中添加 jq，或脚本改用 grep/sed

### 问题 #6: 基础镜像有残留数据 [中等]
- **现象**: 容器启动后已有 test-project 和 mohist.db（来自之前测试）
- **根因**: 基础镜像在构建时包含了测试运行产生的数据
- **建议**: 基础镜像应只包含工具和环境，不应有用户数据；或在 entrypoint 中清理

### 问题 #7: 无日志文件 [中等]
- **现象**: `~/.mohist/logs/` 为空，无法通过日志诊断问题
- **根因**: 未确认（可能是日志级别或路径配置问题）
- **建议**: 确保日志写入文件，提供日志查询 API

---

## 可观测性改进建议

1. `/api/agent/status` 返回空值 — 应返回结构化信息（有无 agent 运行、历史 agent 状态）
2. agent spawn 失败时应有明确的错误信息和日志
3. `mo issue show` 应包含失败原因（为什么 blocked）
4. 缺少 `mo server status` 类命令查看运行时状态
5. 日志文件为空 — server 和 agent 的日志应持久化到文件
