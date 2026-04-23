# E2E Walkthrough: Mohist 完整流程验证

**日期**: 2026-04-22
**目标**: 验证 fix-plan-stage-tasks-generation 修复后的完整流程
**状态**: 已完成（未走通完整流程，在 build 阶段失败）

---

## 进度记录

### Step 1: Build ✅
- `npm run build` 成功
- `npm test` 全部通过（42 test suites, ~400+ tests）
- 无错误

### Step 2: Server ✅
- Server 已在运行，PID 1039601，端口 3456
- Health check: OK
- 存在 7 个历史 issue（#1-#7），均为 blocked/draft/active 状态

### Step 3: Create Issue ✅
- Issue #8 创建成功: "E2E验证-添加time命令"
- 状态: draft/active
- API 返回正常

### Step 4: Start Issue ✅
- Issue #8 开始处理
- CLI 返回成功

### Step 5: Monitor Loop - Design Phase ✅
- Plan stage 成功生成全部 4 个 artifact：proposal.md, design.md, specs/time-command/spec.md, tasks.json
- 到达审批点 waiting-design-review
- Artifact 质量良好：
  - proposal.md: 包含 Why/What Changes/Capabilities/Impact 四节
  - design.md: 包含 Context/Goals/Decisions/Risks 四节
  - specs: ADDED 格式，包含 3 个 Requirement 和 Scenario
  - tasks.json: 3 个任务 (T-001, T-002, T-003)，依赖关系正确，有 acceptanceCriteria
- **修复效果确认**: plan stage artifact 生成正常，全部 4 个 artifact 一次性生成成功
- 工作树路径: `/home/surac/.mohist/projects/mohist/worktrees/issue-8/`
- 从 start 到 awaiting approval 约 2 分钟（15:06:42 → 15:16:35）

### Step 6: Approve Design Review ✅
- `mo issue approve 8` 成功
- Agent 已恢复，进入 build + implement 阶段

### Step 7: Monitor Loop - Implementation Phase ❌
- Approve 后 pipeline 立即失败，issue 回滚到 draft/blocked
- 诊断过程极其困难：
  1. `mo issue logs 8` 返回 "No logs found" — 没有 agent 日志
  2. server.log 只有 EPIPE 错误，没有 pipeline 错误
  3. workflow_log 表中没有 build 阶段的事件记录
  4. 需要 kill 并重启 server 加临时 console.error 调试才定位到问题

- **根因**: 两个独立问题叠加：
  1. **tasks.json passes=true 问题**: plan 阶段 agent 的 self-review 环节修改了 tasks.json，将所有任务的 `passes` 字段改为 `true`。这导致 RalphExecutor 的 `findNextPendingTask` 跳过所有任务，build 阶段以 `completed=0, total=3` 退出 → `success: false` → pipeline 失败
  2. **spawn opencode ENOENT**: 重启 server 后，新 server 进程的 PATH 不包含 opencode，导致 ACP session 启动失败

- 诊断耗时约 30 分钟，主要时间花在找不到错误信息

---

## 发现的问题

### 问题 #1: Plan 阶段 self-review 修改 tasks.json 导致 build 跳过所有任务 [严重]
- **现象**: Approve design review 后，build 阶段立即失败，返回 "Build completed with 0 tasks executed out of 3 total"
- **根因**: Plan 阶段的 self-review agent 在审查 artifacts 时修改了 tasks.json，将 `passes: false` 改为 `passes: true`。RalphExecutor 读取 tasks.json 后认为所有任务已完成，跳过执行
- **证据**:
  - tasks.json 中 3 个任务全部 `passes: true`
  - RalphExecutor 的 `findNextPendingTask` 过滤 `passes: true` 的任务
  - `runPipelineBuildStage` 第 489 行检查 `completed === 0 && total > 0` 返回失败
- **建议**: 
  1. Plan 阶段 self-review prompt 应明确禁止修改 tasks.json 的 passes 字段
  2. 或在 build 阶段开始前重置所有任务的 passes 为 false
  3. 或将 self-review 的输出写入独立的审查文件，不允许修改源 artifacts

### 问题 #2: Pipeline 错误不可观测 [严重]
- **现象**: Pipeline 失败后，没有任何可观测的错误信息：
  - `mo issue logs` 返回空
  - server.log 没有错误
  - workflow_log 没有 build 阶段事件
  - API 只返回 draft/blocked 状态，没有错误消息
- **根因**: 
  1. `executePipeline` 的错误通过 `log.error` 输出，但 log 系统写入的文件不是 server.log（stderr 指向 server.log，但 log 系统写入不同的文件路径）
  2. `runPipelineBuildStage` 的 "0 tasks executed" 是正常的返回路径（不是异常），但 `completed=0` 被视为失败
  3. 失败消息存在 `result.message` 中，但没有暴露给 API 或 CLI
- **建议**:
  1. Pipeline 失败时将错误消息存入 issue 的 approvalState 或新增 errorState 字段
  2. `mo issue show` 应显示失败原因
  3. API 应在 issue 数据中包含最后的错误信息
  4. 确保所有 pipeline 日志输出到可访问的位置

### 问题 #3: Server 重启后 opencode 不在 PATH 中 [中等]
- **现象**: 重启 server 后，ACP session 启动失败：`spawn opencode ENOENT`
- **根因**: 新 server 进程的 PATH 环境变量不包含 opencode 安装路径
- **建议**: 使用 `resolveOpencodeBinPath()` 返回的绝对路径来 spawn opencode，而非依赖 PATH

## 可观测性改进建议
1. **Pipeline 结果暴露**: issue API 应包含最后一次 pipeline 运行的结果（成功/失败/原因）
2. **日志可发现性**: `mo server status` 显示的日志路径应该与实际写入路径一致
3. **失败原因查询**: `mo issue show` 对 blocked 状态的 issue 应显示失败原因
4. **workflow_log 覆盖**: build 阶段的开始/完成/失败都应有 workflow_log 事件（当前只在 detectOpenSpecChange 找到 change 后才记录）
5. **mo issue logs 改进**: 应该从 workflow_log 表读取与 issue 相关的关键事件（session start/complete, task start/complete, build start/complete/fail），而不仅仅读取 agent 会话日志
