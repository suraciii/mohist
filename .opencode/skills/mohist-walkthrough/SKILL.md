---
name: mohist-walkthrough
description: Mohist 项目 E2E walkthrough 流程验证，使用容器隔离环境。走一遍完整的 mohist 工作流（build → container → server → create issue → start → monitor → approve → done），自动监控进度、发现问题、记录到 walkthrough/ 目录。当需要验证 mohist 流程、测试工作流端到端、或走一遍 dev 流程时使用。触发词包括 "walkthrough"、"走流程"、"验证流程"、"e2e 测试"、"端到端测试"。
---

# Mohist E2E Walkthrough (Container-based)

在容器中走一遍完整的 mohist 工作流，验证流程能否走通。发现问题、分析原因、记录结果。

**你是 QA，只负责发现和记录问题，绝对不做任何修复。** 遇到 bug 时，深入追根因并记录，然后继续流程或标记流程阻塞。不要修改任何源码、配置、数据库或产物文件来绕过问题。

## 原则

**只观测不修复。** 修改代码、修改产物文件、修改数据库都属于越权行为。

**优先通过 API 观测系统状态。** CLI 命令和 HTTP API 是公开接口，直接查数据库是最后手段。频繁绕过 API 说明可观测性不足——记录之。

**可观测性不足即问题。** 无法有效诊断时，记录"可观测性不足"作为发现。

**每个问题追到根因。** 无法定位时标记"未定位"并记录已排除的方向。

## 容器环境

使用 `test/agentic/` 共享容器基础设施：

```
test/agentic/
├── shared/
│   ├── Containerfile    # 基础容器 (FROM mohist-test)
│   └── entrypoint.sh    # 启动 mo-server
└── verify-e2e-walkthrough/
    ├── TESTPLAN.md      # Walkthrough 测试计划
    └── scripts/         # 辅助脚本
```

容器规格：
- User: `motest`
- Workspace: `/app/workspace/`
- Data: `/home/motest/.mohist/`
- mohist source: `/opt/mohist-src` (built)
- Server: `localhost:3456` (entrypoint 自动启动)
- 工具: `mo`, `mo-server`, `node`, `git`, `curl`（注意: 无 `jq`、无 `opencode`）

**Layer 要求**: 基础镜像 (Layer A) 包含 mohist 核心但不包含 `opencode`。完整 pipeline（需要 agent spawn）需要 Layer B 镜像。如果仅测试 infra 层（server、project、issue CRUD），Layer A 足够。测试完整 pipeline 需要先构建 Layer B 镜像。

使用 `podman`（首选）或 `docker` 运行容器。

## 记录目录

每次 walkthrough 记录到 `walkthrough/` 目录，独立文件：

```
walkthrough/<YYYY-MM-DD>-<HHMMSS>-<简短标识>.md
```

## 流程

```
create testplan → build image → run container → create issue → start issue
                                                                   │
                                              ┌──→ monitor loop ←──┘
                                              │        │
                                              │    正常推进 / 异常
                                              │        │
                                              │    approve → analyze → 记录
                                              │        │
                                              │    done → collect → cleanup → 总结
```

### Step 1: 准备

确保 `test/agentic/verify-e2e-walkthrough/` 目录存在且包含 TESTPLAN.md 和辅助脚本。同时创建记录文件 `walkthrough/<YYYY-MM-DD>-<HHMMSS>-<标识>.md`。

### Step 2: 构建容器镜像

```bash
# 检查基础镜像是否存在
podman images mohist-test --format '{{.Repository}}'

# 构建 walkthrough 镜像
podman build \
  -t mohist-walkthrough \
  -f test/agentic/shared/Containerfile \
  test/agentic/
```

如果基础镜像 `mohist-test` 不存在，参考 `test/agentic/shared/Containerfile` 注释构建。

### Step 3: 启动容器

```bash
CONTAINER_NAME="mohist-wt-$(date +%Y%m%d-%H%M%S)"
RESULT_DIR="$(pwd)/walkthrough"

# 注意:
# - 不映射端口 (-p)，通过 podman exec 在容器内操作，避免与主机端口冲突
# - 使用 sleep infinity 保持容器存活（entrypoint 启动 server 后需前台进程）
podman run -d \
  --name "$CONTAINER_NAME" \
  -v "$RESULT_DIR:/app/results:z" \
  mohist-walkthrough \
  sleep infinity

# 验证 server 就绪（容器内 curl，不依赖 jq）
podman exec "$CONTAINER_NAME" curl -sf http://localhost:3456/api/health
```

将容器名记录到 walkthrough 文件中。

### Step 4: Walkthrough 流程

所有命令通过 `podman exec` 在容器内执行：

```bash
# CLI 命令
podman exec "$CONTAINER_NAME" mo issue list

# API 调用（无 jq，直接输出 JSON）
podman exec "$CONTAINER_NAME" curl -s http://localhost:3456/api/health

# 执行辅助脚本
podman exec "$CONTAINER_NAME" bash /opt/mohist-src/test/agentic/verify-e2e-walkthrough/scripts/check-status.sh 1

# 检查容器内进程
podman exec "$CONTAINER_NAME" ps aux
```

#### 4a: 初始化环境

容器首次使用需要配置 git 和创建干净的 project：

```bash
# 配置 git（容器内无默认 git identity）
podman exec "$CONTAINER_NAME" git config --global user.email "motest@test.local"
podman exec "$CONTAINER_NAME" git config --global user.name "motest"
```

#### 4b: 创建 Project

```bash
# 创建 project（需要至少一个 commit，否则 worktree 创建会失败）
podman exec "$CONTAINER_NAME" bash -c '\
  mkdir -p /app/workspace/walkthrough-test && \
  cd /app/workspace/walkthrough-test && \
  git init && \
  git commit --allow-empty -m "Initial commit" && \
  mo project create walkthrough-test --path /app/workspace/walkthrough-test && \
  mo project use walkthrough-test'
```

#### 4c: 创建并启动 Issue

```bash
podman exec "$CONTAINER_NAME" mo issue create "E2E walkthrough test" --body "验证完整工作流"
podman exec "$CONTAINER_NAME" mo issue start 1
```

### Step 5: 监控循环

定期（建议 30s 间隔）检查 issue 状态：

```bash
podman exec "$CONTAINER_NAME" mo issue show <issue-number>
```

直到出现：
- 到达审批点 → 执行审批，继续监控
- 状态变为 blocked/draft → 分析失败原因
- 状态长时间无变化 → 检查 agent 进程、日志

监控时关注：状态是否推进？产物是否生成？agent 进程是否存活？

### Step 6: 问题分析

发现异常时的诊断手段：

```bash
# 容器内日志
podman exec "$CONTAINER_NAME" ls -la /home/motest/.mohist/logs/
podman exec "$CONTAINER_NAME" cat /home/motest/.mohist/logs/*.log

# Agent 进程
podman exec "$CONTAINER_NAME" ps aux

# API 状态
podman exec "$CONTAINER_NAME" curl -s http://localhost:3456/api/agent/status
podman exec "$CONTAINER_NAME" curl -s http://localhost:3456/api/issues/<id>

# Worktree 产物
podman exec "$CONTAINER_NAME" ls -laR /home/motest/.mohist/projects/
```

### Step 7: 收集结果

```bash
# 收集日志
podman exec "$CONTAINER_NAME" bash -c 'cp -r /home/motest/.mohist/logs/ /app/results/logs-$(date +%Y%m%d)/'

# 收集数据库快照（最后手段）
podman exec "$CONTAINER_NAME" bash -c 'cp /home/motest/.mohist/mohist.db /app/results/mohist-$(date +%Y%m%d).db'

# 收集 worktree 产物
podman exec "$CONTAINER_NAME" bash -c 'cp -r /home/motest/.mohist/projects/ /app/results/projects-$(date +%Y%m%d)/'
```

### Step 8: 清理

```bash
podman stop "$CONTAINER_NAME"
podman rm "$CONTAINER_NAME"
```

### Step 9: 总结

更新 `walkthrough/` 记录文件，汇总所有发现。

## 辅助脚本

脚本位于 `test/agentic/verify-e2e-walkthrough/scripts/`，在容器内通过 `podman exec` 执行。

注意: 容器内无 `jq`，脚本不应依赖 `jq`。使用 `grep`/`sed`/`python3` 替代。

### scripts/create-project.sh

```bash
#!/bin/bash
set -euo pipefail

PROJECT_NAME="${1:?Usage: create-project.sh <name>}"
PROJECT_PATH="/app/workspace/${PROJECT_NAME}"

git config --global user.email "motest@test.local" 2>/dev/null || true
git config --global user.name "motest" 2>/dev/null || true

mkdir -p "$PROJECT_PATH"
cd "$PROJECT_PATH"
git init
git commit --allow-empty -m "Initial commit"

mo project create "$PROJECT_NAME" --path "$PROJECT_PATH"
mo project use "$PROJECT_NAME"

echo "Project $PROJECT_NAME created and activated"
```

### scripts/check-status.sh

```bash
#!/bin/bash
set -euo pipefail

ISSUE_ID="${1:?Usage: check-status.sh <issue-id>}"

echo "=== Issue #${ISSUE_ID} ==="
mo issue show "$ISSUE_ID" 2>&1 || echo "(show command failed)"
echo ""
echo "=== Agent Processes ==="
ps aux | grep -E "opencode|mo-server" | grep -v grep || echo "No agent processes"
echo ""
echo "=== Server Health ==="
curl -sf http://localhost:3456/api/health || echo "(health check failed)"
```

### scripts/collect-logs.sh

```bash
#!/bin/bash
set -euo pipefail

DEST="/app/results"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"

mkdir -p "$DEST"

if [ -d /home/motest/.mohist/logs/ ]; then
    cp -r /home/motest/.mohist/logs/ "$DEST/logs-${TIMESTAMP}/"
    echo "Logs collected to $DEST/logs-${TIMESTAMP}/"
else
    echo "No logs directory found"
fi

if [ -f /home/motest/.mohist/mohist.db ]; then
    cp /home/motest/.mohist/mohist.db "$DEST/mohist-${TIMESTAMP}.db"
    echo "Database snapshot: $DEST/mohist-${TIMESTAMP}.db"
fi
```

## 记录文件格式

```markdown
# E2E Walkthrough: <简短标题>

**日期**: YYYY-MM-DD HH:MM
**目标**: 本次 walkthrough 的目标
**状态**: 进行中 | 已完成 | 阻塞
**容器**: <container-name>

---

## 进度记录

### Step N: <阶段名> ✅/❌/⚠️
- 结果和关键发现

---

## 发现的问题

### 问题 #N: <标题> [严重/中等/低]
- **现象**: ...
- **根因**: ...（或标记"未定位"）
- **证据**: ...
- **建议**: ...

## 可观测性改进建议
- （记录诊断过程中发现的信息缺口）
```
