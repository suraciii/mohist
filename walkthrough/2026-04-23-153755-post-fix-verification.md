# E2E Walkthrough: Post-Fix Verification

**日期**: 2026-04-23 15:37
**目标**: 在 fix-e2e-walkthrough-failures 和 fix-plan-stage-tasks-generation 修复后，重新走完整流程验证
**状态**: 已完成

---

## 进度记录

### Step 1: Build ✅
- `npm run build` 成功
- 有一个非阻塞 warning：`[MODULE_TYPELESS_PACKAGE_JSON]` 关于 package.json 缺少 `"type": "module"`

### Step 2: Server ✅
- `node bin/mo server start` 启动成功，端口 3456
- `/api/health` 返回正常
- ⚠️ 日志文件名日期异常：`2026-04-15T094218.log`，今天是 2026-04-23

### Step 3: Create Issue ✅
- `mo issue create "Add hello world greeting function" --body "..."` 成功
- Issue #9 创建，状态 draft/active

### Step 4: Start Issue ✅
- `mo issue start 9` 成功
- 状态从 draft → plan

### Step 5: Monitor Loop ✅

**Plan 阶段** (~5 min):
- 约 5 分钟后到达审批点，状态 plan/awaiting
- Self-review notes 完整且有意义
- Agent 正确生成 proposal、specs、tasks

**Plan 审批 → Build**:
- `mo issue approve 9` 成功
- 状态跳转到 build（plan 审批后直接进入 build，跳过 design，符合预期）

**Build 阶段** (~4 min):
- Agent 在 worktree 中生成了 `src/utils/greeting.ts` 和 `src/utils/greeting.test.ts`
- 约 4 分钟后进入 review 阶段

**Review 阶段** (~1 min):
- 到达 review 审批点，self-review verdict: PASS
- Review 报告内容合理

**Review 审批 → Done**:
- `mo issue approve 9` 成功
- 状态变为 done/active
- Agent 正常停止

### 整体流程验证
```
draft → plan(awaiting) → build → review(awaiting) → done
          ↑ approve              ↑ approve
```
**Pipeline 完整走通。**

---

## 发现的问题

### 问题 #1: `mo issue diff` 失败 — worktree 基于错误 git 历史 [严重]

- **现象**: `mo issue diff 9` 报错 `fatal: master...mo/issue-9: no merge base`
- **根因**: 多层问题叠加：

  **(A) 项目注册时 base_branch 检测错误**
  - `detectBaseBranch()` (`src/git/detect-base-branch.ts:7-30`) 在 `git symbolic-ref refs/remotes/origin/HEAD` 失败时 fallback 到硬编码 `'main'`
  - 项目注册时，可能 `origin/HEAD` 尚未设定，导致存储 `base_branch = 'main'`
  - 实际 remote HEAD 分支是 `master`，不是 `main`

  **(B) Remote 上存在 stale 的 `origin/main` 分支**
  - GitHub repo `suraciii/mohist.git` 的 `main` 分支是一个已删除的旧分支（29462 commits，来自 fork 的上游项目如 opencode），本地 remote-tracking ref 未清理
  - `origin/main` 与 `master`（307 commits，mohist 自身历史）**无共同祖先**，是两条完全独立的 git 历史

  **(C) Worktree 基于错误的 `origin/main` 创建**
  - `WorktreeManager.create()` (`src/git/worktree-manager.ts:120`) 使用 `origin/${baseBranch}` = `origin/main` 作为起点
  - 因为 stale 的 `origin/main` 存在，`branchExists` 返回 true，没有报错
  - 结果 `mo/issue-9` 分支从 origin/main（无关项目）创建，而非从 master（mohist 自身）

  **(D) `mo issue diff` 使用不同的 base branch 解析逻辑**
  - CLI 的 `getDefaultBranch()` (`src/cli/commands/issue.ts:11-30`) 通过 `origin/HEAD` 解析为 `master`
  - 而 worktree 创建用的是 DB 中的 `base_branch = 'main'`
  - 两套独立的 base branch 解析逻辑产生不一致

  **(E) `propose.ts:65` 未传 baseBranch**
  - `propose.ts` 调用 `worktreeManager.create()` 时省略了 `baseBranch` 参数，使用默认值 `'main'`
  - 即使 DB 中存了正确值，propose 路径也会用错误值

- **证据**:
  - DB: `base_branch = 'main'`，但 `origin/HEAD` → `master`
  - `git ls-remote --heads origin main` 返回空（远程已无 main）
  - `git merge-base origin/main origin/master` → NO MERGE BASE
  - worktree 中 `mo/issue-9` 和 `master` 无共同祖先
  - `git remote show origin` 标记 `origin/main` 为 stale，所有 `mo/issue-*` 显示 "merges with remote main"

- **影响**:
  1. `mo issue diff` 完全不可用
  2. Agent 在错误的代码基础上工作（基于无关项目的 29462 commits）
  3. `git fetch --prune` 后，`origin/main` 被清除，后续新 worktree 创建会直接报错 `Branch 'main' not found`

- **建议**:
  1. **修复 `detectBaseBranch`**：fallback 值应基于实际存在的分支，不应硬编码。应尝试检测 `main` → `master` → 当前 HEAD 分支
  2. **统一 base branch 解析**：消除 `getDefaultBranch()` 和 `detectBaseBranch()` 两套独立逻辑，使用 DB 中存储的 `base_branch` 作为唯一真相源
  3. **添加 stale ref 清理**：在 worktree 创建前执行 `git fetch --prune` 或检查远程分支是否真正存在
  4. **修复 `propose.ts:65`**：传入 `project.baseBranch`
  5. **worktree 创建后验证**：检查新分支与 base branch 是否有共同祖先

### 问题 #2: `mo issue logs` 始终无内容 [中等]

- **现象**: `mo issue logs 9` 返回 "No logs found for issue #9"
- **根因**: 未定位。Agent 明显在工作（状态在推进、产物在生成），但没有日志被记录到可查询的位置
- **证据**: 整个 pipeline 过程中多次执行 `mo issue logs 9`，始终返回空
- **建议**: 检查 agent 日志写入路径是否与 `mo issue logs` 的读取路径一致

### 问题 #3: Server 日志文件日期异常 [低]

- **现象**: Server 启动时日志文件名为 `2026-04-15T094218.log`，实际日期是 2026-04-23
- **根因**: 未深入定位。可能是日志文件名使用了缓存的旧值而非当前时间
- **证据**: `mo server status` 显示 `Logs: /home/surac/.mohist/logs/2026-04-15T094218.log`

### 问题 #4: `mo issue show` 审批后仍显示旧阶段 self-review notes [低]

- **现象**: 进入 build 阶段后，`mo issue show` 仍显示 plan 阶段的 self-review notes
- **根因**: 可能是设计如此（保留上一个审批点的 notes），但显示上可能造成混淆
- **证据**: build 阶段时 `Approval: approved (stage: plan)` 和 plan 的 self-review notes 仍然可见

## 可观测性改进建议

1. **Agent 日志不可见**: 整个 pipeline 过程中无法通过 `mo issue logs` 获取任何 agent 工作日志。只能通过 API (`/api/agent/status`) 看到 agent 是否在运行，看不到在做什么。建议增加更丰富的 agent 活动日志。
2. **Build 阶段进度不可见**: Build 阶段花费约 4 分钟，期间无法知道 agent 在哪个 task 上工作、进度如何。建议增加 task 级别的状态可见性。
3. **Worktree 信息缺失**: `mo issue show` 不显示 worktree 路径和分支信息，需要手动查找才能定位产物。建议在 issue show 中增加 worktree 位置。
