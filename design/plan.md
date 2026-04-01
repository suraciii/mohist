# PLAN Stage

## 职责

基于明确需求设计方案、分解任务。

PLAN stage 专注于技术方案的制定。需求已在 Explore Mode 中梳理清晰（参见 pipeline-model spec 的两种交互模式），PLAN 接收的是明确的 Issue 描述。

## 两种执行场景

### 首次执行

Issue 从 `draft` 进入 `plan` 后首次执行：

1. 探索代码库理解技术上下文
2. 基于明确需求设计方案
3. 分解任务并输出计划

### 修复执行

Issue 从 `check` 回到 `plan` 后修复执行：

1. 分析 CHECK stage 的审查报告
2. 制定修复计划

## 工具集

- `read`: 阅读代码库、理解上下文
- `ask_user`: 向用户询问小问题（信息缺失、歧义）
- `write`: 输出方案文档和任务清单

## 产出物

- 技术方案文档
- 任务清单（供 BUILD stage 执行）

## Gate

默认配置 `gate_after: human`：PLAN 完成后暂停，等待用户确认方案后再进入 BUILD。

用户可以在 gate 处：
- 批准方案 → 进入 BUILD
- 要求修改 → 回到 PLAN
- 标记大问题 → blocked，退出 Pipeline 回到 Explore Mode

## Stage 结构

```
PLAN {
  jobs: [
    { agent: "architect", task: "设计方案" }
  ]
  gate_after: human
}
```

M1/M2 阶段只有单个 architect-agent Job。M3 可扩展为多 Job（如 architect 设计 + reviewer 审查并行）。
