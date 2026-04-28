# Review Pipeline 缺陷分析

**日期**: 2026-04-27
**触发**: Issue #30 实现审查发现遗漏
**范围**: mohist 工作流 Build → Review → Done 全链路

## 发现

### 审查 Issue #30 发现的 5 个遗漏

| # | 问题 | 严重程度 |
|---|------|---------|
| 1 | `formatRelativeTime` 缺少月份格式 (`>=30天 → "Xmo ago"`) | 功能缺陷 |
| 2 | 颜色值与 spec 不一致 (feature `#16a34a` vs spec `#22c55e` 等) | 视觉偏差 |
| 3 | Spec 要求导出 `getTypeColor`，未实现 | spec 合规 |
| 4 | Area labels 列表与 spec 不一致 | 部分标签不着色 |
| 5 | T-004 tasks.json 状态未更新 | 元数据 |

### 根因：三层防线全部失效

```
┌─────────────────────────────────────────────────────────────────────┐
│  第 1 层: Build Task Agent                                          │
│  acceptance criteria 渲染为 - [ ] checklist                        │
│  result.success = "session 没崩溃"，与 AC 无关                     │
│  ❌ AC 是装饰，不是门禁                                            │
│                                                                     │
│  第 2 层: Review Agent                                              │
│  buildReviewerPrompt 不包含 specs/ 目录                            │
│  review.md 只有 4 个维度: Correctness/Complexity/Test/Security     │
│  ❌ 没有 Spec Compliance 维度                                      │
│  ❌ Review Agent 完全不知道 spec 写了什么                           │
│                                                                     │
│  第 3 层: Review Self-Check                                         │
│  review-self-check.md 只检查格式                                    │
│  ❌ 不验证内容正确性                                                │
└─────────────────────────────────────────────────────────────────────┘
```

### 第 1 层详情：Acceptance Criteria 不验证

`context-assembler.ts:122-128` 把 AC 渲染为：
```
Acceptance Criteria:
  - [ ] getLabelStyle('bug') 返回 { bg: '#fee2e2', text: '#ef4444', size: 'md' }
```

`ralph-executor.ts:470` 判定成功：
```typescript
if (result.success) {  // = ACP session 正常结束
  taskSuccess = true;
  updateTaskInList(tasks, nextTask.id, { passes: true, ... });
}
```

Agent 看到 AC 视为参考目标，完成代码即 commit。颜色值自选 Tailwind 调色板而非 spec 精确值，无机制阻止。

### 第 2 层详情：Review Agent 无 spec 上下文

`artifact-prompt.ts:136-154` buildReviewerPrompt 只传入 issue info + review.md 指令。

对比 buildTaskContext 给 Build Agent 的：
- `[Proposal]` ← Review Agent 拿不到
- `[Design]` ← Review Agent 拿不到
- `[Current Requirement: specs/...]` ← Review Agent 拿不到

### 系统性 Bug：tasks.json 最后一个 task 永远 passes=false

**100% 复现**：所有 18 个 change 的最后一个 task 都是 passes=false。

根因时序：
1. Agent commit（此时 tasks.json 里当前 task 仍是 passes=false）
2. Ralph 更新 tasks.json（passes=true，写入磁盘，未提交）
3. 没有后续 task 来 commit 这个更新
4. mergeBack 时 `git add -- ':!openspec/changes/'` 排除 openspec 目录
5. 最终更新丢失

涉及的代码位置：
- `ralph-executor.ts:470-473` — 更新在 commit 之后
- `worktree-manager.ts:187` — mergeBack 排除 openspec/changes/

## 改进方向

### 1. Review Agent 加入 Spec Compliance 维度

改 `review.md`，加第五维度：
- 逐条对照 acceptance criteria
- 验证精确值（hex 颜色、函数签名、返回值）
- 发现偏差标为 FAIL

改 `buildReviewerPrompt`，注入 specs/ 目录内容到 context。

### 2. Build 后加 AC 验证步骤

在 `ralph-executor.ts` task 完成后，可选发第二个 prompt：
"Verify all acceptance criteria are met. For each criterion, state PASS or FAIL with evidence."

### 3. 前端测试基础设施

`packages/cli/web` 加 vitest，至少覆盖工具函数（label-colors.ts, relative-time.ts）。
对精确值加断言（颜色 hex、时间格式）。

### 4. tasks.json 写入时序修复

方案 A：ralph 更新 tasks.json 后追加 commit
方案 B：mergeBack 不排除 tasks.json（用更精确的排除规则）
方案 C：ralph 在 agent commit 之前更新 tasks.json（让 agent 读到 passes=true 并自行 commit）

### 5. Self-Check 加内容级验证

`review-self-check.md` 增加：验证 review report 的 Correctness 维度是否逐条引用了 acceptance criteria。
