# E2E Walkthrough: Mohist 完整流程验证

**日期**: 2026-04-22
**目标**: 启动 dev mohist，走一遍完整流程，验证流程能否走通
**状态**: 进行中

---

## 进度记录

### Step 1: Build ✅
- `npm run build` 成功编译
- 有一个 MODULE_TYPELESS_PACKAGE_JSON 警告（非阻塞）

### Step 2: Server Start ✅
- Server 已在运行 (PID: 629984, Port: 3456, Uptime: 11h30m)
- `mo server status` 正常返回
- `mo status` 正常返回

### Step 3: 创建 Issue ✅
- `mo issue create "E2E测试-添加hello命令" -b "..."` → Issue #6
- Stage: draft, Status: active
- 已有 5 个旧 issue（#1-#5），状态各异

### Step 4: 首次启动处理 Issue ❌
- `mo issue start 6` → Stage: plan, Status: active
- Worktree 创建成功: `~/.mohist/projects/mohist/worktrees/issue-6/`
- OpenSpec change 目录创建: `openspec/changes/6-e2e-hello/`
- **根因**: `spawn opencode ENOENT` — opencode 二进制不在 PATH 中
- Agent runner 的 fallback 逻辑没有生效（旧 server 进程缺少正确路径）
- Issue 卡在 `plan/active`
- 详见下方问题 #1

### Step 5: Server 重启 + 重试 ✅ (部分)
- `mo server stop` → `mo server start` (PID: 853499)
- `mo issue resume 6` → 回到 draft → `mo issue start 6` → plan/active
- ACP 进程成功启动 (PID: 854457)
- **Round 1 (proposal)**: 成功生成 `proposal.md`
- **Round 2 (specs)**: 成功生成 `specs/hello-command/spec.md`
- **Round 3 (design)**: ACP session 以 success 结束，但没有生成 design.md
- ACP session 持续 289713ms (~4.8 min) 后完成
- 但 5 个 round + self-review 需要的产物只生成了 2 个
- pipeline 又卡死在 `plan/active`
- 详见下方问题 #2

### Step 6: 再次重试
- [ ] 恢复 issue 并重新 start

### Step 7: 审批流程
- [ ] 等待审批点
- [ ] approve / reject

### Step 8: 完成
- [ ] 验证 stage 到达 done

---

## 发现的问题

### 问题 #1: spawn opencode ENOENT [严重] — 已定位
- **现象**: 首次 `mo issue start 6` 后，workflow_log 显示 `spawn opencode ENOENT`
- **根因**: 旧 server 进程（运行了 11.5h）的环境变量中没有 opencode 路径
  - `opencodeBinPath` 配置未设置（config.jsonc 没有 binPath）
  - `~/.opencode/bin/opencode` 不在 system PATH 中
  - 代码 fallback 到 `which opencode` 也找不到
- **修复**: 重启 server 后，opencode 路径解析成功
- **建议**: 在 config.jsonc 中显式配置 `opencode.binPath`，或在 PATH 中添加 `~/.opencode/bin/`

### 问题 #2: Agent 自行跳过产物生成 [严重] — 已定位根因
- **现象**: Plan stage multi-round ACP 只生成 2/5 个产物 (proposal + specs)，缺少 design, tasks
- **根因**: Agent (opencode/LLM) 在 specs 回复中明确说 "Skipping design — no cross-cutting concerns"
  - Agent 认为这是一个简单任务，不需要 design.md
  - 但 workflow controller 的 verify() 要求 design.md 存在
  - 后续的 design prompt 发送后，agent 可能只回复了文本而没有执行 write
- **证据**:
  - Agent message: "This is a single-command, zero-dependency addition... Skipping design — no cross-cutting concerns, architectural decisions, or migration needed."
  - 只有 2 次 write tool_call
  - verify() 检测到 design.md 不存在 → planResult.success = false
  - Issue 被回滚到 draft/blocked（这次错误处理生效了）
- **可复现**: 是（每次尝试都只生成 proposal + specs）
- **建议**:
  1. **Prompt 工程**: 在 design prompt 中更强调 "即使简单也必须生成 design.md，可以简化但不可省略"
  2. **Fallback**: 如果 agent 没有生成某个产物，自动生成一个最小化的默认版本
  3. **验证时机**: 在每个 round 的 prompt 中附带前一个产物的内容，让 agent 感知进度
  4. **考虑简化 Plan stage**: 对于简单 issue，可以跳过某些 round（需要 workflow 层面的支持）

### 问题 #3: Pipeline 卡死无自动恢复 [中等]
- **现象**: 首次运行时 Issue 卡在 `plan/active`（server 重启后 agent 状态丢失）
- **影响**: 用户不知道 pipeline 失败了，需要手动检查
- **备注**: 第二次运行后错误处理正确（回滚到 draft/blocked）
- **建议**: 
  1. Server 启动时检查 recoverableIssues，自动恢复或标记为 blocked
  2. 增加 heartbeat/watchdog 机制

### 问题 #4: mo issue start 对非 draft issue 报错 [低]
- **现象**: `mo issue start 6` 在 issue 处于 plan stage 时报错
- **期望**: 应该提供恢复/继续的命令，而非仅报错
- **当前**: 只能通过 `mo issue resume 6` 回到 draft 再 start

### 问题 #3: Pipeline 卡死无自动恢复 [中等]
- **现象**: Issue 卡在 `plan/active` 但没有自动恢复或标记为 blocked
- **影响**: 用户不知道 pipeline 失败了，需要手动检查
- **建议**: 增加 heartbeat/watchdog 机制，检测 agent 长时间无响应时自动标记为 blocked

### 问题 #4: mo issue start 对非 draft issue 报错 [低]
- **现象**: `mo issue start 6` 在 issue 处于 plan stage 时报错
- **期望**: 应该提供恢复/继续的命令，而非仅报错
- **当前**: 只能通过 `mo issue resume 6` 回到 draft 再 start

