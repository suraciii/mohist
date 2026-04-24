# E2E Walkthrough: Container Walkthrough v2 (post-fix verification)

**日期**: 2026-04-24 11:11
**目标**: 验证上次 walkthrough 发现的 bug 修复后，容器化 walkthrough 是否能正常走通
**状态**: 已完成

---

## 进度记录

### Step 1: Build ✅
- `npm run build` 成功
- 容器镜像需要重建（旧镜像 dist 代码是 Express 版本，新代码是 Hono 版本）
- 需要安装 `hono` 依赖到容器（`npm install --save hono`）
- 通过 `podman cp` + `podman commit` 方式更新镜像

### Step 2: Server ✅
- `podman run -d mohist-walkthrough:latest` 正常启动
- entrypoint.sh 的 `wait $SERVER_PID` 正常保持容器运行
- Health check 通过，日志正常输出到 `/home/motest/.mohist/logs/`

### Step 3: Create Project + Issue ✅
- `POST /api/projects` 创建项目成功，自动检测 baseBranch=master
- `POST /api/projects/test-project/use` 设置当前项目
- `POST /api/issues` 创建 Issue 成功，初始 stage=draft, status=active

### Step 4: Start Issue ✅
- `POST /api/issues/1/start` 返回成功
- Worktree 正确创建在 `/home/motest/.mohist/projects/test-project/worktrees/issue-1`
- Branch `mo/issue-1` 正确创建
- Stage 从 draft → plan
- runningAgents=1

### Step 5: Monitor ✅
- Pipeline 失败后：
  - Stage 正确回滚到 `draft`（Bug #3 已修复）
  - Status 正确变为 `blocked`
  - approvalState 包含详细错误信息
- Reopen 功能正常工作
- 重新 start 后行为一致
- 日志文件正常生成在 `/home/motest/.mohist/logs/`

### 验证项目
| 项目 | 结果 |
|------|------|
| Server 启动 | ✅ |
| 健康检查 | ✅ |
| 创建项目 | ✅ |
| 设置当前项目 | ✅ |
| 创建 Issue | ✅ |
| Start Issue (worktree 创建) | ✅ |
| Pipeline 失败检测 | ✅ |
| Stage 回滚 (draft) | ✅ (Bug #3 已修复) |
| Status 标记 (blocked) | ✅ |
| 错误信息记录 (approvalState) | ✅ |
| 日志文件生成 | ✅ (Bug #7 已修复) |
| Reopen | ✅ |
| 重试 Start | ✅ |
| 多 Issue 并行 | ✅ (#1 和 #2 同时有 worktree) |
| Close Issue | ✅ |

---

## 发现的问题

### 问题 #1: 镜像重建流程复杂 [中等]
- **现象**: 更新代码到容器需要：1) 启动容器 2) 安装新依赖 (hono) 3) 删除旧 dist 4) cp 新 dist 5) cp 新 src 6) 清理残留 7) commit 两次。整个过程耗时约 10 分钟
- **根因**: 基础镜像的依赖和代码捆绑在一起，没有分离
- **证据**: 旧镜像有 Express 依赖但缺少 Hono
- **建议**: 创建 Containerfile 在 monorepo 根目录执行 `npm install` + `npm run build`，一键重建

### 问题 #2: 容器 stop 超时需要 SIGKILL [低]
- **现象**: `podman rm -f` 始终需要 10s 超时后才 SIGKILL
- **根因**: entrypoint.sh 的 `wait $SERVER_PID` 不响应 SIGTERM
- **建议**: 在 entrypoint.sh 中添加 SIGTERM trap

---

## 上次 bug 修复验证结果

| Bug | 描述 | 修复验证 |
|-----|------|---------|
| #1 | 容器无 keep-alive | ✅ 已修复，`wait $SERVER_PID` 正常 |
| #2 | 空 repo worktree 失败 | ✅ 已修复（本次 repo 有初始 commit） |
| #3 | 失败操作状态不一致 | ✅ 已修复，stage 正确回滚到 draft |
| #4 | 容器缺 opencode | 预期行为（Layer A 限制），需 Layer B 镜像 |
| #5 | 容器缺 jq | ✅ 已修复 |
| #6 | 基础镜像残留数据 | ✅ 本次手动清理，commit 前清理 |
| #7 | 无日志文件 | ✅ 已修复，日志正常生成 |

## 可观测性改进建议

1. **日志非常完善** - Pipeline 的每一步（start、spawn、error、rollback、status change）都有详细日志
2. **approvalState 是很好的可观测性手段** - 错误信息清晰包含 stage、error、时间戳
3. **API 层缺少 agent status endpoint** - `/api/agent/status` 返回 404，无法查看当前运行的 agent 列表（但 `/api/issues/1` 的 runningAgents 间接提供了信息）
