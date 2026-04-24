# E2E Walkthrough: Edge Cases & Recovery

**日期**: 2026-04-24 12:52
**目标**: 测试边界情况：服务器重启恢复、并发 issue、approval 流程、close/reopen 循环
**状态**: 已完成

---

## 进度记录

### Step 1: 启动容器 + 初始化 ✅
- Server 正常启动，health check 通过

### Step 2: 并发 Issue ✅
- 创建 3 个 issue (#1, #2, #3) 同时 start
- 3 个 worktree 正确创建
- 3 个 pipeline 全部失败后正确回滚到 draft+blocked
- runningAgents 正确计数 1→2→3

### Step 3: Reopen + Restart 循环 ✅
- Reopen 正确恢复到 draft+active
- Start 再次触发 pipeline
- 失败后再次回到 draft+blocked
- 循环可重复

### Step 4: 服务器重启恢复 ✅
- Kill server 后容器退出（entrypoint wait 返回）
- `podman start` 重启容器后 server 重新启动
- 所有 issue 状态正确恢复（draft+blocked, approvalState=error）

### Step 5: Close/Reopen ⚠️
- Close 成功但 status 变为 blocked（不是 closed）
- Close 后 worktree 没有清理（issue-1 worktree + mo/issue-1 分支仍在）
- Reopen 可以恢复

### Step 6: Approve ✅
- 无 pending gate 时正确拒绝
- 错误信息引导用户使用 start

### Step 7: Propose ⚠️
- Propose 可以在 blocked issue 上执行，创建了 change 并启动了 pipeline

### Step 8: Project 切换 ✅
- 创建第二项目、切换、issues 正确隔离
- 删除项目正常

### Step 9: 错误输入 ✅
- 不存在的 issue → 404
- 空 title → 400
- 重复 start → 400/409
- 并发 double-start → 正确防护

### Step 10: Comments ✅
- POST comments 正常
- GET comments 通过 issue detail 返回

### Step 11: Config/Logs/Status API ✅
- `/api/config/list` 返回完整配置
- `/api/logs/tail` 返回日志（167 行）
- `/api/agent/status` 返回 agent 状态

### Step 12: 容器 SIGTERM ✅
- 新容器 0.14-0.25s 优雅停止（3/3 一致）
- 手动 kill server 后容器自动退出（正确行为）

---

## 发现的问题

### 问题 #1: Propose 允许在 blocked issue 上执行 [中等]
- **现象**: `POST /api/propose/:number/propose` 在 status=blocked 的 issue 上成功执行，创建了 change 并启动了 pipeline
- **根因**: propose.ts 的 `post /:number/propose` 路由没有检查 issue status 是否为 blocked
- **证据**: issue #1 status=blocked 时 propose 返回 200 并创建 change "1-issue-a-v2"
- **建议**: 在 propose 路由开头添加 status 检查，blocked 的 issue 需要先 reopen

### 问题 #2: Close 后 worktree 不清理 [低]
- **现象**: `POST /api/issues/:number/close` 后 worktree 目录和 git 分支仍然存在
- **根因**: close 路由只调用 `issueService.block()`，没有调用 `worktreeManager.remove()`
- **证据**: close 后 `git worktree list` 仍显示 issue-1 worktree，`mo/issue-1` 分支仍在
- **建议**: close 时可选清理 worktree（或提供 force 参数），reopen 时重新创建

### 问题 #3: Close 返回 status=blocked 而非 closed [低]
- **现象**: Close API 返回 `status: "blocked"` 而非 `status: "closed"`
- **根因**: `close` 实际调用的是 `issueService.block()`，没有独立的 "closed" 状态
- **证据**: API 响应 `{"success":true,"data":{"issue":{"status":"blocked"}},"message":"Issue #1 closed"}`
- **建议**: 语义上 closed 和 blocked 是不同的：blocked = 运行失败，closed = 用户主动关闭。建议区分或在 API 文档中说明

### 问题 #4: 手动 kill server 后容器退出，无法自动恢复 [低]
- **现象**: 在容器内 `kill $(pgrep -f mo-server)` 后，entrypoint 的 `wait` 返回，bash 退出，容器停止
- **根因**: entrypoint.sh 的 `wait $SERVER_PID` 在 server 死后返回是正常行为
- **证据**: 容器变为 Exited(0)，需要 `podman start` 手动重启
- **建议**: 这是可接受的（容器编排系统会自动重启）。可在 entrypoint 中添加 server 自动重启循环，但可能增加复杂度

## 可观测性改进建议

1. **`/api/agent/status` 端点可用** — 返回 running, activeAgents, waitingQuestions 等完整信息
2. **`/api/logs/tail` 端点可用** — 支持 cursor, truncated 等参数
3. **缺少 `GET /api/issues/:id/comments` 独立端点** — 需要通过 issue detail 间接获取
4. **缺少 `DELETE /api/issues/:id` 端点** — 无法删除 issue
