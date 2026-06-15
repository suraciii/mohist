# Mohist

**Mohist 是面向个人开发者的自治开发流水线（Autonomous Dev Pipeline）。**

你定义"完成"长什么样，Mohist 负责 plan、build、check、integrate，把代码写进你的仓库。你是 owner，不是程序员——把 issue 丢进去，去做别的事，回来收获。

## Mohist 适合你吗？

适合，如果你：

- 是个人开发者 / 独立开发者 / side-project owner
- 想让 AI 真正帮你**完成**功能，不只是补全代码
- 想要可控的工作流（approval gate、review、可恢复），而不是黑盒 agent
- 接受 self-host：装在自己的机器上，数据和代码不出本机

不适合，如果你：

- 想要 AI 帮你打字写代码（用 Cursor / Copilot / Claude Code 更合适）
- 需要团队协作功能（Mohist 当前是单用户产品）
- 不想自己装东西、不想配环境

## 核心特性

- **结构化工作流**：Draft → Plan → Build → Check → Integrate → Done，每个阶段都有产物可审查
- **可控的自治**：approval gate 决定什么时候停下来等你，其他时候自己跑
- **完整的可观察性**：实时看板、活动流、coder session 回放、日志，知道每一刻发生了什么
- **可恢复**：进程崩了、机器重启了、网络断了——workflow 状态不会丢，可以 resume / retry / rerun
- **OpenSpec 留痕**：每个 issue 都产出 proposal / design / specs / tasks / review，可审计
- **可定制 workflow profile**：不同类型的 issue 走不同流程
- **多入口**：Web UI、`mo` CLI、外部 agent skill，挑你顺手的方式

## 快速开始

```bash
# 安装依赖
npm install

# 构建 .NET 后端
npm run build

# 启动 server
npm run dev:server

# 另一个终端启动 runner
npm run dev:runner

# 第三个终端启动 Web UI
npm run dev:web
```

打开 `http://localhost:3456`，按 Web UI 的引导创建第一个项目。

第一次跑通？看 [`docs/getting-started.md`](docs/getting-started.md)。

## 系统要求

- .NET SDK 10.0+
- Node.js 18+，npm 9+
- `opencode` CLI 在 `PATH` 中可用（作为 coder agent）

## 文档

完整文档在 [`docs/`](docs/) 下：

- **[快速上手](docs/getting-started.md)** — 从零到第一个完成的 issue
- **[核心概念](docs/concepts.md)** — Project / Issue / Workflow / Epic / Skill
- **[工作流详解](docs/the-workflow.md)** — 五个阶段分别做什么
- **[Issue 管理](docs/issues.md)** — 创建、启动、审批、恢复、关闭
- **[用 Epic 规划](docs/epics.md)** — 把多个 issue 组织成产品里程碑
- **[Web UI 指南](docs/web-ui.md)** — 看板、详情页、设置
- **[CLI 参考](docs/cli-reference.md)** — `mo` 命令完整说明
- **[Workflow Profile](docs/workflow-profiles.md)** — 定制你的工作流
- **[Runner 指南](docs/runner.md)** — 执行后端怎么跑
- **[Skill 机制](docs/skills.md)** — 用外部 agent 探索需求
- **[故障恢复](docs/troubleshooting.md)** — 失败、blocked、drift 怎么办
- **[Self-host 部署](docs/self-host.md)** — 在你的机器上长跑

## 仓库结构

```
packages/
  server/    ASP.NET Core + Orleans 控制平面
  runner/    TypeScript 执行后端
  web/       React Web UI
  cli/       mo CLI
docs/        用户文档（你正在看的）
design/      架构与设计文档（开发者向）
openspec/    工作流产出的变更产物
```

## 设计哲学

- **Local-first**：你拥有全部数据和代码，不依赖外部 SaaS
- **Guarded autonomy**：默认自治运行，关键点显性提示
- **Audit trail**：每个决策、每次改动都有可追溯的产物
- **Workflow as product**：workflow profile 是核心产品对象，不是配置项

详见 [`design/architecture.md`](design/architecture.md)（开发者向）。

## 开发与贡献

见 [`docs/CONTRIBUTING.md`](docs/CONTRIBUTING.md)。

## License

MIT
