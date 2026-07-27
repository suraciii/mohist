# Mohist

Mohist 是面向个人开发者、对 Agent 友好的 AI 软件生产线控制系统。

它把产品想法整理成可执行的 issue，让 Agent 按 workflow 持续完成规划、实现、检查和集成。Agent 是 owner 的代理人，可以进入流水线上原本由人负责的位置。质量不靠 owner 盯住每个交付点，而靠清晰输入、自动检查、审查任务、审批决策、失败恢复和必要时的人工升级共同保障。

用户通常在 Slack、IDE 或其他已有场所与外部 Agent 协作。外部 Agent 通过 Mohist Skill
和 `mo` 查询、委托和操作执行层，再把结果带回原来的交互场所。Mohist Web 是备用操作和
可视化平面，用于查看全局状态、核对执行证据和人工接管，不是另一个工作站点。

## 产品界面

- **Mohist Skill + `mo`**：外部 Agent 使用 Mohist 的默认交互路径；人也可以直接使用同一套命令。
- **Web UI**：备用操作和可视化平面；关键操作完整，但不承载日常对话。
- **通知**：把需要关注的变化推到用户已有的聊天工具，不拥有执行状态。

## 工作流

Workflow profile 定义 issue 怎么进入生产线，阶段、任务、检查和审批点均可配置。默认 profile（`mohist/local`）：

```
Draft → Plan → Build → Check → Integrate → Done
```

多个 issue 同时推进、各自独立。Plan / Check 等关键阶段停在审批点，收到 approve / reject 后继续流动。详见 [Workflow Profile](docs/workflow-profiles.md)。

## 事件响应

workflow、issue、epic、runner、agent session 都产生事件。Agent 事件路由让你配置 Agent 自动响应：代理审批、分析失败、汇总进展、生成后续 issue、通知 owner。详见 [Agent 事件路由](docs/event-routing.md) 与 [Agent 监管](docs/agent-supervision.md)。

## 实装状态

| ✅ 可用 | 🚧 接线中 | 💭 方案（spec 已定稿） |
|---|---|---|
| 五阶段 Workflow、审批点、Epic 自动推进 | Agent 监管预设、评论 @提及、issue 关注 | 复合与子 issue |
| `mo` CLI、Web UI、Hermes 通知、事件路由 | Profile collection 迁移 | 移动端 PWA |
| OpenCode / Pi runtime、GitHub PR profile | | 可观测性 |

🚧 / 💭 项由对应 issue 推进落地，见各篇「实装差距」。

<!-- TODO: 补 Web UI 截图 -->

## 文档

从 [快速上手](docs/getting-started.md) 开始；产品方向见 [产品愿景](docs/vision.md)；完整阅读路径见 [文档索引](docs/README.md)。架构与设计文档在 [`design/`](design/README.md)。

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
