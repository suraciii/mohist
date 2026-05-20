# Workflow Approval Simplification

## 探索背景
- 默认 workflow YAML 暴露了 `approvalEvidence`，用于标记 verification、verdict、candidate 三类审批证据。
- 这个形态让用户以为 approval 需要额外声明前置证据，而不是自然依赖 stage checks。

## 关键发现
- 用户视角下，`approval: true` 的直觉语义应该是：当前 stage 的所有 tasks 完成、所有 checks 通过后，才进入人工审批。
- `approvalEvidence` 混合了两个职责：审批前置条件和 stale/snapshot 防护。前者可以由 “all checks passed” 直接表达。
- 对通用 workflow 来说，要求用户标注 `verification`、`verdict`、`candidate` 会把 Mohist 内部审批模型泄漏到 YAML。

## 可视化
```text
stage
  tasks[] ── all completed ┐
                            ├── checks[] ── all passed ── approval
  dynamic tasksFrom ────────┘

code.changed
  └── on event reset checks-and-approval
      ├── reset affected tasks
      ├── reset checks
      └── clear approval
```

## 决策与结论
- Workflow 定义、编译结果和运行时都不再支持 `approvalEvidence`。
- Approval 的前提统一为当前 stage 的所有 checks passed。
- 代码变更导致的 stale/重跑由 `on.code.changed.reset` 负责表达。
- `verification`、`verdict`、`candidate` 不再是 Mohist workflow 的内置审批角色。

## 开放问题
- 如果未来需要更强的 snapshot 绑定，应优先考虑 check 自己的输出约束或通用 stale/reset 语义，而不是重新引入审批专用 evidence role。
