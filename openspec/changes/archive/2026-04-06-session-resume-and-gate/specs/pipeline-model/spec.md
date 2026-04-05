## MODIFIED Requirements

### Requirement: 默认 Pipeline 配置
系统 SHALL 提供内置默认 Pipeline 配置。默认配置中 build 和 check 阶段均配置 approval（表示 plan gate 和 check gate）。

```yaml
stages:
  - stage: plan
    prompt: '分析 issue #{issue.number}: {issue.title}，探索 codebase，产出实现计划'
    approval: false
  - stage: build
    prompt: '按 plan 阶段的计划实现 {issue.title}。计划摘要：{plan.output}'
    approval: true
  - stage: check
    prompt: '检查 {issue.title} 的实现：运行测试、lint、typecheck，报告问题'
    approval: true
```

approval 语义：当阶段 S 配置 `approval: true` 时，表示进入 S 前需要用户审批（等价于上一阶段的 gate_after: human）。

#### Scenario: 无 workflow.yaml 时使用默认
- **WHEN** 项目没有 workflow.yaml 配置
- **THEN** 使用内置默认 3 stage pipeline
- **AND** build 和 check 阶段均有 approval: true

#### Scenario: plan gate（build approval）
- **WHEN** plan 阶段执行完成
- **AND** build 阶段配置 approval: true
- **THEN** agent 停止，等待用户审批
- **AND** 用户审批后进入 build

#### Scenario: check gate（check approval）
- **WHEN** build 阶段执行完成
- **AND** check 阶段配置 approval: true
- **THEN** agent 停止，等待用户审批
- **AND** 用户审批后进入 check
