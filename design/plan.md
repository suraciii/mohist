# PLAN Stage

## 职责

基于 proposal.md 做技术设计、拆分任务。

PLAN stage 接收 Explore Mode 产出的 proposal.md，探索代码库理解技术上下文，产出技术方案和行为规格，并拆分为可执行的任务清单。

## 两种执行场景

### 首次执行

Issue 从 Explore 进入 `plan` 后首次执行：

1. 读取 proposal.md 理解需求
2. 探索代码库理解技术上下文
3. 产出 specs/ (行为规格 delta)
4. 产出 design.md (技术设计)
5. 产出 tasks.json (任务清单)
6. 自审查拆分质量

### 修复执行

Issue 从 `check` 回到 `plan` 后修复执行：

1. 读取 Check stage 的审查报告
2. 基于报告更新 design.md 和 tasks.json

## 输入

- `proposal.md` — Explore Mode 产出（Intent/Scope/Approach/User Stories）

## 产出物

### specs/ (行为规格)

Delta specs 描述系统行为的变更：

```
specs/{capability}/spec.md
  ## ADDED Requirements
  ### Requirement: {name}
  #### Scenario: {name}
  - GIVEN {context}
  - WHEN {action}
  - THEN {expected}
```

遵循 OpenSpec delta 格式：ADDED / MODIFIED / REMOVED。

### design.md (技术设计)

- Technical Approach — 技术方案概述
- Architecture Decisions — 关键技术决策及理由
- Module Design — 每个模块：名称/职责/接口
- Data Flow — 数据流和组件交互
- File Changes — 涉及的文件

### tasks.json (任务清单)

```json
{
  "tasks": [
    {
      "id": "T-001",
      "order": 1,
      "type": "WRITE",
      "mode": "AFK",
      "title": "...",
      "description": "... (写给 AI 的执行指令)",
      "output": "... (完成时存在什么)",
      "dependsOn": [],
      "files": ["..."],
      "patterns": ["..."],
      "acceptanceCriteria": ["..."]
    }
  ]
}
```

Task 类型：WRITE / TEST / MIGRATE / CONFIG / REVIEW
执行模式：AFK（AI 独立完成）/ HITL（需要人类决策）

## Task 拆分原则

1. **垂直切片** — 每个 task 应穿透所有相关层（不是只改 DB 或只改 UI）
2. **一次会话一个 task** — 如果一个 task 不能在一次 AI 会话内完成，说明太粗
3. **交错测试** — Schema → Logic → API → UI，tests 穿插在各层之间，不是最后才测
4. **AFK 优先** — 尽量让 task 可自动执行，只在真正需要人类决策时标记 HITL
5. **Task-as-Prompt** — task 描述是写给 AI 的指令，指定文件范围、模式引用、完成定义

## 自审查

自审查是人类审查的预演——AI 按照人类在 gate 处的同样标准先审一遍，把明显问题修掉，降低人类审查成本。

审查维度：

**方案完整性：**
- proposal 中的每个 user story 是否有对应的 spec 和 task？
- 所有 edge case 和 failure mode 是否覆盖？
- 依赖的外部系统/模块是否已识别？
- Out of Scope 是否明确？

**拆分质量：**
- 每个 task 是否可在一个 AI 会话内完成？
- 每个 task 是否有明确的 output？
- 依赖关系是否形成 DAG？无环？
- tests 是否交错在 writes 之间？
- AFK/HITL 标记是否合理？

最多 3 轮迭代。未通过的问题自动修复后重新审查。

## Gate

默认配置 `gate_after: human`：自审查通过后，将方案 + 自审查报告展示给用户，等待批准或反馈。

- 批准 → 进入 BUILD
- 给反馈 → AI 修改后重新自审查，再展示
- 标记大问题 → blocked，退回 Explore Mode

## Stage 结构

```
PLAN {
  jobs: [
    { agent: "planner", task: "设计方案+拆分任务" }
  ]
  gate_after: human
}
```

M1/M2 阶段只有单个 planner-agent Job。M3 可扩展为多 Job（如 architect 设计 + reviewer 审查并行）。
