# CHECK Stage

## 职责

跑测试套件、代码审查、对比需求。

CHECK stage 提供独立于 BUILD 的反馈视角，是循环模型的关键反馈点。

## 审查者角色

| 角色 | 职责 | 关注点 |
|------|------|--------|
| architect-agent | 架构审查 | 设计一致性、模块边界、接口设计 |
| qa-agent | 测试审查 | 测试覆盖率、边界用例、回归风险 |
| code-reviewer-agent | 代码审查 | 代码质量、命名、错误处理 |

M1/M2 阶段先实现单 agent 审查，M3 扩展为多 agent 并行审查。

## 并行 Job 模型

CHECK stage 的多 Job 设计：

```
CHECK {
  jobs: [
    { id: architect,  agent: "architect-agent" },
    { id: qa,         agent: "qa-agent" },
    { id: reviewer,   agent: "code-reviewer-agent" }
  ]
  gate_after: human
}
```

三个 Job 并行执行，全部完成后汇总审查报告。

Job 依赖图：

```
architect ──┐
qa ─────────┼──→ 汇总报告 ──→ gate
reviewer ───┘
```

## 工具集

- `read`: 阅读代码变更、需求文档
- `bash`: 运行测试套件

## 产出物

- 审查报告（各角色的审查结论）
- 问题列表（如果有）

## 循环机制

CHECK 完成后两种路径：

1. **通过** → Issue stage 变为 `done`
2. **有问题** → Issue stage 从 `check` 回到 `plan`，PLAN 基于审查报告制定修复计划

这个循环对应 DevOps pipeline 的反馈周期：CHECK 发现实现与方案的偏差 → 回到 PLAN 重新规划 → BUILD 重新实现 → CHECK 再次检查。

## Gate

默认配置 `gate_after: human`：CHECK 完成后暂停，让用户确认最终结果。

- 审查通过 → 用户确认 → `done`
- 审查有问题 → 用户选择是否接受自动回到 PLAN 的建议
