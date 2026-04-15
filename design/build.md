# BUILD Stage

## 职责

执行 tasks.json 中的任务、写代码、跑测试。

BUILD stage 是代码实现阶段。Agent 根据 PLAN stage 产出的 tasks.json 逐个执行。

## AFK vs HITL

每个 task 有执行模式：

| 模式 | 行为 | 例子 |
|------|------|------|
| AFK (Away From Keyboard) | AI 独立完成，自动流转 | "实现 SearchService" |
| HITL (Human-In-The-Loop) | 到决策点暂停，等人类决策后继续 | "确认数据库 schema 迁移方案" |

### AFK task 执行

```
读 task 描述 → 探索相关代码 → 实现 → 测试 → 验证 output → 标记完成
```

### HITL task 执行

```
读 task 描述 → 探索相关代码 → 到决策点 → 暂停，ask_user →
人类决策 → 基于决策继续实现 → 测试 → 验证 output → 标记完成
```

## 内循环

BUILD 的核心机制是自主内循环：

```
write → test → fix → test → ...
```

1. 写代码实现任务
2. 跑测试验证
3. 如果测试失败，分析错误并修复
4. 重跑测试
5. 直到任务完成或遇到无法解决的问题

Agent 在内循环中自主迭代，不需要人工干预。如果遇到小问题（信息缺失），通过 `ask_user` 询问后继续。

## Task 执行机制

每个 task 通过独立的 ACP session 执行。mohist agent 通过 tool 从 tasks.json 获取当前应执行的任务（按 order、依赖关系、状态过滤），将 task 描述作为 prompt 传给 coder session。

```
mohist agent (orchestrator)
  │
  ├── get_next_task() → T-003 (type: WRITE, mode: AFK, dependsOn 满足)
  │
  └── run_acp_session(task: T-003) → 独立上下文执行
        │
        ├── 成功 → 标记 T-003 完成 → get_next_task()
        └── 失败 → 分类处理（重试/跳过/报告用户）
```

Task 描述是自包含的 prompt（包含文件范围、模式引用、完成定义），coder session 不需要自己解析 tasks.json。

## 工具集

- `read`: 阅读代码、理解实现上下文
- `write`: 写代码、修改文件
- `bash`: 运行测试、执行构建命令

## 产出物

- 代码变更（文件修改、新增文件）
- 测试结果

## Gate

默认配置 `gate_after: none`：BUILD 完成后自动进入 CHECK，无需人工确认。

BUILD 的产出由 CHECK stage 独立审查，不需要中间人工节点。

## Stage 结构

```
BUILD {
  jobs: [
    { agent: "coder", task: "执行 tasks.json" }
  ]
  gate_after: none
}
```

M1/M2 阶段只有单个 coder-agent Job。
