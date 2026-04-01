# BUILD Stage

## 职责

执行任务、写代码、跑测试。

BUILD stage 是代码实现阶段。Agent 根据 PLAN stage 输出的任务清单逐个执行。

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
    { agent: "coder", task: "实现任务" }
  ]
  gate_after: none
}
```

M1/M2 阶段只有单个 coder-agent Job。
