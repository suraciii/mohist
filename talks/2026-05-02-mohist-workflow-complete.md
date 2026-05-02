# mohist 工作流完整图

**更新日期**: 2026-05-02
**核心变更**: Gate 概念被 Check 取代

---

## 工作流总览

```
用户
 │
 │ 创建 Issue
 ▼
┌────────────────────────────────────────────────────────────────────┐
│ Explore Mode (Pipeline 外)                                          │
│ 自由对话，梳理需求，产出清晰的 Issue 描述                              │
│ (重大问题 → blocked，回到 Explore)                                  │
└────────────────────────────────────────────────────────────────────┘
 │
 │ mo issue start
 ▼
┌────────────────────────────────────────────────────────────────────┐
│ Stage: PLAN                                                         │
│ 目的: 生成完整的设计方案                                             │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Tasks (执行):                                                      │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ 1. Generate Proposal  ──▶ 调用 AI 生成 proposal.md          │    │
│  │ 2. Generate Specs     ──▶ 调用 AI 生成 specs/               │    │
│  │ 3. Generate Design    ──▶ 调用 AI 生成 design.md            │    │
│  │ 4. Generate Tasks     ──▶ 调用 AI 生成 tasks.json           │    │
│  │ 5. Self-Review        ──▶ 调用 AI 生成 self-review.md       │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  Checks (验收标准):                                                  │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ ✅ proposal-complete      检查 proposal.md 是否完整          │    │
│  │ ✅ specs-complete         检查 specs/ 是否覆盖需求           │    │
│  │ ✅ design-complete        检查 design.md 是否合理            │    │
│  │ ✅ tasks-valid            检查 tasks.json 是否可执行         │    │
│  │ ✅ self-review-passed     检查自审查是否通过                 │    │
│  │ ⏳ user-approval          检查用户是否已审批                 │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  推进条件: 所有 Checks 通过                                          │
│                                                                     │
│  失败处理:                                                           │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ • 交付物不完整 → retry-task (重新生成)                       │    │
│  │ • 自审查不通过 → escalate to Explore (重新梳理需求)           │    │
│  │ • 用户拒绝     → escalate to Explore (需求方向错误)           │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  下一个 Stage: BUILD (当所有 checks 通过)                             │
└────────────────────────────────────────────────────────────────────┘
 │
 │
 ▼
┌────────────────────────────────────────────────────────────────────┐
│ Stage: BUILD                                                        │
│ 目的: 执行 tasks.json 中的任务，实现代码                               │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Tasks (执行):                                                      │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ 按 DAG 顺序执行:                                              │    │
│  │ • Task 001  ──▶ 调用 Coder Agent 实现功能                     │    │
│  │ • Task 002  ──▶ 调用 Coder Agent 实现功能                     │    │
│  │ • Task 003  ──▶ 调用 Coder Agent 实现功能                     │    │
│  │ • ...                                                       │    │
│  │                                                             │    │
│  │ 每个 Task 内部: write → test → fix → test → ... (内循环)     │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  Checks (验收标准):                                                  │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ ✅ all-tasks-complete     检查所有 task 是否已完成            │    │
│  │ ✅ code-compiles          检查代码是否能编译                  │    │
│  │ ⏳ user-approval          (Build 阶段无用户审批)              │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  推进条件: 所有 Checks 通过                                          │
│                                                                     │
│  失败处理:                                                           │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ • Task 失败     → retry-task (重新执行)                       │    │
│  │ • 编译失败      → auto-fix (AI 修复)                         │    │
│  │ • 所有 Task 失败 → escalate to PLAN (重新设计)                │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  下一个 Stage: CHECK (当所有 checks 通过)                             │
└────────────────────────────────────────────────────────────────────┘
 │
 │
 ▼
┌────────────────────────────────────────────────────────────────────┐
│ Stage: CHECK                                                        │
│ 目的: 验证代码质量，确保可以合并                                       │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Tasks (执行):                                                      │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ 1. Run Build-Test   ──▶ npm run build && npm test           │    │
│  │ 2. Run AI Review    ──▶ 调用 Review Agent 做 6-pass 审查     │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  Checks (验收标准):                                                  │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ ✅ build-test-passed      检查编译和测试是否通过              │    │
│  │ ✅ ai-review-passed       检查代码审查是否通过                │    │
│  │ ⏳ user-approval          检查用户是否已审批合并               │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  推进条件: 所有 Checks 通过                                          │
│                                                                     │
│  失败处理:                                                           │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ • build-test 失败 → auto-fix (AI 修复，最多2次)              │    │
│  │                   → 修复失败 → escalate to BUILD            │    │
│  │ • ai-review 失败  → auto-fix (按 fix suggestions 修复)       │    │
│  │                   → 修复失败 → escalate to PLAN (设计缺陷)    │    │
│  │ • 用户拒绝        → escalate to PLAN (修复)                  │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  下一个 Stage: DONE (当所有 checks 通过)                              │
└────────────────────────────────────────────────────────────────────┘
 │
 │
 ▼
┌────────────────────────────────────────────────────────────────────┐
│ Stage: DONE                                                         │
│ 目的: 合并代码，关闭 Issue                                            │
├────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Tasks (执行):                                                      │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ Merge Queue:                                                  │    │
│  │ 1. rebase onto master                                         │    │
│  │ 2. run build verification                                     │    │
│  │ 3. fast-forward merge                                         │    │
│  │ 4. delete worktree                                            │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  Checks (验收标准):                                                  │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ ✅ merge-successful       检查合并是否成功                    │    │
│  │ ✅ build-verify-passed    检查 rebase 后构建是否通过          │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  失败处理:                                                           │
│  ┌────────────────────────────────────────────────────────────┐    │
│  │ • 合并冲突      → auto-rebase (AI 解决冲突)                   │    │
│  │ • rebase 后失败 → escalate to BUILD (重新实现)               │    │
│  └────────────────────────────────────────────────────────────┘    │
│                                                                     │
│  Issue 关闭，合并完成!                                               │
└────────────────────────────────────────────────────────────────────┘
```

---

## 循环机制

```
        ┌──────────────────────────────────────────┐
        │                                          │
        │   ┌──────────┐    ┌──────────┐          │
        └──▶│   PLAN   │───▶│  BUILD   │          │
            └──────────┘    └────┬─────┘          │
                 ▲               │                │
                 │               ▼                │
                 │          ┌──────────┐          │
                 │          │  CHECK   │          │
                 │          └────┬─────┘          │
                 │               │                │
                 │         通过  │                │
                 │               ▼                │
                 │          ┌──────────┐          │
                 │          │   DONE   │          │
                 │          └──────────┘          │
                 │                                │
                 │         失败                   │
                 └────────────────────────────────┘

CHECK 失败路径:
• build-test 失败 → 回到 BUILD (代码修复)
• ai-review 发现设计问题 → 回到 PLAN (重新设计)
• 用户拒绝合并 → 回到 PLAN (按反馈修复)
```

---

## 核心设计理念 (2026-05-02)

### 1. Check 取代 Gate

**旧设计 (已废弃)**:
```
Stage {
  jobs: [...],
  gate_after: human | none   ← gate 是 stage 属性
}
```

**新设计**:
```
Stage {
  tasks: [...],              ← 执行单元
  checks: [...]              ← 验收标准 (包含 user-approval)
}
```

user-approval 从"gate"降级为"check 列表中的一个 check 项"，与其他 checks 平级。

### 2. 所有阶段统一用 Check 推进

| Stage | Tasks (执行) | Checks (验收) | 推进条件 |
|-------|-------------|--------------|---------|
| **Plan** | 生成 proposal/specs/design/tasks/self-review | proposal-complete, specs-complete, design-complete, tasks-valid, self-review-passed, **user-approval** | 所有 checks pass |
| **Build** | 按 DAG 执行 tasks | all-tasks-complete, code-compiles | 所有 checks pass |
| **Check** | 运行 build-test, ai-review | build-test-passed, ai-review-passed, **user-approval** | 所有 checks pass |
| **Done** | Merge Queue | merge-successful, build-verify-passed | 所有 checks pass |

### 3. 反应式设计

每个 Check 定义失败后的反应策略：

```typescript
interface Check {
  name: string;
  verify(ctx): CheckResult;     // 验证逻辑
  onFailure: Reaction;           // 失败反应
}

type Reaction = 
  | { type: 'retry-task', target: string, maxRetries: number }   // 重试任务
  | { type: 'auto-fix', maxRetries: number }                      // AI 自动修复
  | { type: 'escalate', to: Stage }                              // 回退到上一阶段
  | { type: 'ask-user' }                                          // 暂停等待用户
```

### 4. "不用人来守护执行"

- mohist **持续自动推进**，只在需要人类介入时暂停
- 暂停的唯一原因：**user-approval check 未通过**（或 Reaction 配置为 ask-user）
- 其他所有 checks 失败都通过 **auto-fix / retry / escalate** 自动处理

---

## 执行引擎伪代码

```typescript
class StageRunner {
  async run(ctx: StageContext): Promise<StageRunResult> {
    // 1. 执行所有 Tasks
    for (const task of this.tasks) {
      await task.execute(ctx);
    }
    
    // 2. 验证所有 Checks
    for (const check of this.checks) {
      const result = await check.verify(ctx);
      
      if (result.status === 'fail') {
        // 3. 触发 Reaction
        const reactionResult = await this.handleReaction(check.onFailure, ctx);
        
        if (!reactionResult.resolved) {
          return {
            success: false,
            escalateToStage: this.getEscalateStage(check)
          };
        }
      }
    }
    
    // 4. 所有 checks 通过
    return {
      success: true,
      nextStage: this.nextStage
    };
  }
}
```

---

## 相关文件

- `design/plan.md` — Plan stage 设计（已更新移除 Job 概念）
- `design/build.md` — Build stage 设计（已更新移除 Job 概念）
- `design/check.md` — Check stage 设计（已更新移除 Job 概念）
- `talks/2026-04-01-stage-model.md` — Stage 模型初始设计（已更新移除 Job 概念）
- `talks/2026-05-02-workflow-check-vs-step.md` — 本次讨论记录
