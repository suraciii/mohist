# Mohist

Mohist 是一个面向个人开发者的本地优先软件生产系统，用于扩大软件产出的规模。

它通过可自定义的工作流推进每个工作单元（issue），多条工作流可以并行运行，各阶段自动执行并留下可审查的记录。

## 工作流

工作流由 workflow profile 定义，阶段、任务、检查均可自定义。默认 profile（`mohist/default`）：

```
Draft → Plan → Build → Check → Integrate → Done
```

- **Draft** —— issue 创建后的初始状态
- **Plan** —— 理解需求，产出设计、规格、任务清单
- **Build** —— 写代码、跑测试
- **Check** —— 复核产出
- **Integrate** —— 合并回主分支

多个 issue 可以同时推进，各自独立。详见 [Workflow Profile](docs/workflow-profiles.md)。

<!-- TODO: 补 Web UI 截图 -->

## 文档

- [快速上手](docs/getting-started.md)
- [核心概念](docs/concepts.md) —— Project / Issue / Workflow / Epic / Skill
- [工作流详解](docs/the-workflow.md)
- [Issue 管理](docs/issues.md)
- [用 Epic 规划](docs/epics.md)
- [Web UI 指南](docs/web-ui.md)
- [CLI 参考](docs/cli-reference.md) —— `mo` 命令
- [Workflow Profile](docs/workflow-profiles.md)
- [Runner 指南](docs/runner.md)
- [Skill 机制](docs/skills.md)
- [故障恢复](docs/troubleshooting.md)
- [Self-host 部署](docs/self-host.md)

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

见 [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md)。

## License

MIT
