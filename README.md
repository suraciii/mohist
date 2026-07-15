# Mohist

Mohist 是面向个人开发者的 AI 软件生产线控制系统。

它把产品想法整理成可执行的 issue，让 Agent 按 workflow 持续完成规划、实现、检查和集成。Agent 是 owner 的代理人，可以进入流水线上原本由人负责的位置。质量不靠 owner 盯住每个交付点，而靠清晰输入、自动检查、审查任务、审批决策、失败恢复和必要时的人工升级共同保障。

## 工作流

Workflow profile 定义 issue 怎么进入生产线。阶段、任务、检查和审批点均可配置。默认 profile（`mohist/local`）：

```
Draft → Plan → Build → Check → Integrate → Done
```

- **Draft** —— issue 创建后的初始状态，尚未进入生产线
- **Plan** —— 理解需求，产出设计、规格、任务清单
- **Build** —— 写代码、跑测试
- **Check** —— 复核产出
- **Integrate** —— 合并回主分支

多个 issue 可以同时推进，各自独立。Plan / Check 等关键阶段会进入审批点，收到 approve / reject 决策后继续流动。详见 [Workflow Profile](docs/workflow-profiles.md)。

## 事件响应

Mohist 的 workflow、issue、epic、runner 和 agent session 都会产生事件。Agent 事件路由让你配置 Agent 响应这些事件：代理审批、分析失败、汇总完成内容、生成后续 issue 或通知 owner。代理审批只是其中一个场景。

<!-- TODO: 补 Web UI 截图 -->

## 文档

从 [快速上手](docs/getting-started.md) 开始；完整阅读路径见 [文档索引](docs/README.md)。架构与设计文档在 [`design/`](design/README.md)。

## 仓库结构

```
packages/
  server/    控制平面（ASP.NET Core + Orleans）
  runner/    执行平面（TypeScript）
  web/       Web UI（React）
  cli/       mo CLI
docs/        用户文档
design/      架构与设计文档
openspec/    工作流产出的变更产物
```

## 贡献

见 [`CONTRIBUTING.md`](CONTRIBUTING.md)。

## License

MIT
