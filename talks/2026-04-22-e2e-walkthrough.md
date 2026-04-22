# E2E Walkthrough: Mohist 完整流程验证（第二轮）

**日期**: 2026-04-22
**目标**: 使用修复后的代码重新走一遍完整流程，验证 Plan stage 产物生成修复是否生效
**状态**: 进行中

---

## 进度记录

### Step 1: Build ✅
- `npm run build` 成功，包含新模板骨架文件的拷贝
- 构建产物正常输出到 `dist/`

### Step 2: Server Start ✅
- 停止旧 server (PID: 943843, uptime 4h)
- 启动新 server (PID: 1039601, Port: 3456)
- 6 个旧 issue (#1-#6) 均为 blocked 状态，未触发异常恢复

### Step 3: 创建 Issue ✅
- `mo issue create "E2E验证-添加version命令"` → Issue #7
- Stage: draft, Status: active

### Step 4: Start Issue → Plan Stage ✅
- `mo issue start 7` → plan/active
- ACP 进程成功 spawn，session 创建成功 (sessionId: ses_24b55a9c...)
- **Round 1 (proposal)**: 18:09 → 18:13 ✅ 生成 proposal.md (1300 bytes)
- **Round 2 (specs)**: 18:13 → 18:14 ✅ 生成 specs/version-command/spec.md (1599 bytes)
- **Round 3 (design)**: 18:14 → 18:15 ✅ 生成 design.md (2291 bytes)
- **Round 4 (tasks)**: 18:15 → 18:18 ✅ 生成 tasks.json (2355 bytes)
- **Round 5 (self-review)**: 18:17 → 18:19 ✅ 完成，发现 T-001 缺少 command-registry.ts 并修复
- **总耗时**: 589,970ms (~9.8 min)
- **到达审批点**: Pipeline paused at gate, stage: plan ✅

### Step 5: Plan Stage 审批 → Build Stage 失败 ❌
- `mo issue approve 7` → "Issue #7 approved, agent resumed"
- Build stage 启动，但 6ms 内完成，0 个任务被执行
- Issue 回滚到 draft/blocked
- 详见下方问题 #5（Ralph loop 因 attempts 缺失跳过所有任务）

### Step 6: 流程中断
- Build stage 因 #5 bug 无法推进，流程在此中断

---

## 发现的问题

### 问题 #5: Ralph loop 因 tasks.json 缺少 `attempts` 字段跳过所有任务 [严重] — 已定位
- **现象**: Build stage approve 后，Ralph loop 在 6ms 内完成，报告 `completed:0, failed:2, total:2, success:false`
  - 没有 "Task attempt started" 日志
  - 没有 ACP spawn 日志
  - tasks.json 被修改为 `passes: true, error: "Skipped: no attempts made (maxRetries=0)"`
  - 但 Ralph loop 仍然计数 failed:2

- **根因**: `ralph-executor.ts:385` 的 for 循环条件使用了 `nextTask.attempts`
  ```typescript
  for (let attempt = nextTask.attempts + 1; attempt <= maxRetries + nextTask.attempts; attempt++)
  ```
  - Agent 生成的 tasks.json 没有 `attempts` 字段
  - `nextTask.attempts` 为 `undefined`
  - `undefined + 1 = NaN`, `NaN <= NaN` 为 `false`
  - 循环体从未执行
  - 随后 501 行 `attemptsUsed === nextTask.attempts` → `undefined === undefined` → `true`
  - 任务被错误标记为 `passes: true`（实际未执行）
  - 但 505 行仍然 `failed++`

- **证据**:
  - 日志: `Ralph loop completed completed:0 failed:2 total:2 success:false duration:6`
  - tasks.json: `"passes": true, "error": "Skipped: no attempts made (maxRetries=0)"`
  - 无任何 "Task attempt started" 或 ACP spawn 日志
  - `ralph-executor.ts:385` 循环条件在 attempts=undefined 时产生 NaN 比较

- **影响范围**: 所有由 agent 生成 tasks.json 的场景都可能触发（agent 通常不生成 `attempts` 字段）

- **建议**:
  1. **立即修复**: `readTasks()` 或 `runRalphLoop()` 中为缺少 `attempts` 的 task 设置默认值 0
  2. **防御性编程**: 循环条件改为 `const baseAttempts = nextTask.attempts ?? 0`
  3. **tasks.json schema 验证**: 在读取时验证必需字段，缺失字段补默认值
  4. **501-503 行逻辑问题**: 跳过时标记 `passes: true` 不合理——应该标记为 `passes: false` 或单独的 skipped 状态

- **相关代码**:
  - `packages/cli/src/openspec/ralph-executor.ts:155-169` — readTasks()
  - `packages/cli/src/openspec/ralph-executor.ts:379-385` — attempts 变量和循环条件
  - `packages/cli/src/openspec/ralph-executor.ts:500-504` — skip 逻辑

### 问题 #6: workflowLogRepo.insert FOREIGN KEY constraint failed [中等] — 已定位
- **现象**: Build stage 的 `build_started` 和 `build_failed` 事件写入 workflow_log 时报 `FOREIGN KEY constraint failed`
- **根因**: `writeTaskLog` 传入的 `issueId` 使用 `String(context.issueNumber ?? context.issueId ?? '')` (即 `"7"`)，但 workflow_log 表的 issueId 外键可能期望 UUID 格式（实际 issue.id 是 `13e44188-2d26-4fe0-bcef-6a697fb4ad9d`）
- **证据**: 日志 `WARN: workflowLogRepo.insert failed eventType:build_started issueId:7 error:FOREIGN KEY constraint failed`
- **影响**: workflow_log 丢失事件记录，可观测性降低
- **建议**: 统一 issueId 格式，确保写入 workflow_log 时使用 UUID

### 问题 #7: CLI `mo issue show` 不显示 approval state [低]
- **现象**: Issue #7 到达审批点后（`approvalState.status: "awaiting"`），CLI `mo issue show` 仍显示 `Stage: plan, Status: active`
- **根因**: CLI 的 show 命令没有渲染 approvalState 信息
- **证据**: API `/api/issues/7` 返回 `approvalState.status: "awaiting"`，但 CLI 输出无任何审批相关提示
- **影响**: 用户无法通过 CLI 判断 issue 是否需要审批
- **建议**: 在 `mo issue show` 输出中添加审批状态信息

### 问题 #8: Plan stage 产物生成 — 修复验证成功 ✅
- **验证**: 之前的问题 #2b（tasks.json 未生成）现在已修复
- **证据**: 
  - Issue #7 的 Plan stage 成功生成全部 4 个产物 (proposal, specs, design, tasks)
  - tasks.json 质量高：结构清晰，有 acceptance criteria，依赖关系正确
  - self-review round 自动执行并修复了 T-001 的遗漏
  - 总耗时 ~10 分钟，节奏合理
- **结论**: 结构化 prompt（XML 分区 + 模板骨架）修复有效

---

## 之前的发现（第一轮 walkthrough，#1-#4 保留供参考）

### 问题 #1: spawn opencode ENOENT [严重] — 已修复 ✅
- 旧 server 进程缺少 opencode 路径，重启后解决

### 问题 #2/#2b: Plan stage 产物跳过/缺失 [严重] — 已修复 ✅
- 结构化 prompt 修复有效，4/4 产物全部成功生成

### 问题 #3: Pipeline 卡死无自动恢复 [中等] — 已修复 ✅
- Server 启动时自动恢复 orphaned issues

### 问题 #4: mo issue start 对非 draft issue 报错 [低]
- 未修复，仍需要 resume → start 两步操作

---

## 可观测性改进建议

1. **CLI 显示审批状态**: `mo issue show` 应显示 approvalState，让用户知道是否需要操作
2. **Ralph loop 任务跳过时的日志**: 当 for 循环因 NaN 不执行时，应有明确的 WARN 日志
3. **tasks.json schema 验证**: 读取时验证必需字段（attempts, passes），缺失时补默认值并 warn
4. **workflow_log issueId 一致性**: 统一使用 UUID 或 issue number，避免外键约束失败
