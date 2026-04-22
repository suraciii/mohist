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

### Step 6: 第三次尝试（修复后）✅ (部分)
- 修复 design.md prompt（强制生成）后重试
- Server 启动时成功恢复 4 个 orphaned issues (#2-#5)
- `mo issue reopen 6` → `mo issue start 6` → plan/active
- ACP 进程启动 (PID: 944894)
- **Round 1 (proposal)**: 成功 ✅
- **Round 2 (specs)**: 成功 ✅
- **Round 3 (design)**: 成功 ✅（46行高质量 design.md）
- **Round 4 (tasks)**: 失败 ❌（tasks.json 未生成）
- ACP session 持续 924314ms (~15.4 min) 后关闭
- pipeline 回滚到 draft/blocked
- 详见下方问题 #2b

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

### 问题 #2: Agent 自行跳过产物生成 [严重] — 已修复 ✅
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
- **修复**: 
  - ✅ 修改 design.md prompt：移除 skip 许可，改为强制指令 "You SHALL always generate this file"
  - ✅ 修改 self-review.md prompt：将 design.md 从可选改为必须
- **验证**: 第三次尝试中 design.md 成功生成（46行高质量内容）
- **状态**: 已修复，design.md 不再被跳过

### 问题 #2b: tasks.json 未生成 [严重] — 新发现
- **现象**: Plan stage 前 3 个产物（proposal, specs, design）成功生成，但 tasks.json 缺失
- **根因调查**:
  1. **Prompt 分析**: tasks.md prompt 明确说 "Create the tasks.json file" 和 "Write the file to `{changeDir}/tasks.json`"
  2. **工具可用性**: `write_file` 工具可用，agent 在前几轮成功使用过
  3. **可能原因**: 
     - Agent 在 tasks round 只回复了文本说明，没有执行 write_file tool call
     - 或者 agent 认为 tasks 应该以不同格式生成（如 tasks.md 而非 tasks.json）
     - 或者 agent 在长时间运行后（~15分钟）出现疲劳/错误
- **时间线**: 
  - 14:04:15 - tasks round 开始
  - 14:14:16 - ACP session 关闭（10分钟后），报告 tasks.json 未找到
- **建议**:
  1. **增强 prompt**: 在 tasks prompt 中更强调 "必须使用 write_file 工具写入 tasks.json"
  2. **增加示例**: 提供完整的 tasks.json 示例，减少 agent 困惑
  3. **验证前检查**: 在 verify 之前检查 agent 是否实际执行了写操作
  4. **Timeout 调整**: tasks round 耗时 10 分钟，可能 agent 在超时前未完成

### 问题 #3: Pipeline 卡死无自动恢复 [中等] — 已修复 ✅
- **现象**: Issue 卡在 `plan/active` 但没有自动恢复或标记为 blocked
- **影响**: 用户不知道 pipeline 失败了，需要手动检查
- **修复**: 
  - ✅ 添加 `AgentRunnerService.recoverIssues()` 方法
  - ✅ Server 启动时自动调用，将 orphaned issues 标记为 blocked + 回滚 draft
  - ✅ 日志记录恢复动作
- **验证**: Server 启动时成功恢复 4 个 issues (#2 plan, #3 plan, #4 plan, #5 build)
- **状态**: 已修复

### 问题 #4: mo issue start 对非 draft issue 报错 [低]
- **现象**: `mo issue start 6` 在 issue 处于 plan stage 时报错
- **期望**: 应该提供恢复/继续的命令，而非仅报错
- **当前**: 只能通过 `mo issue resume 6` 回到 draft 再 start
- **建议**: UX 改进，提供 `mo issue continue` 或自动 resume 功能

