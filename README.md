# mohist

Mohist 是一个本地运行的 AI 开发工作流系统：Server 负责状态与编排，Runner 负责工作区副作用，Web UI 和 CLI 负责观察与操作。

当前主线实现基于 ASP.NET Core + Orleans + SQLite，并通过外部 coder agent 执行代码、git 和工作区相关动作。

## 当前能力

- Issue 工作流：`Draft -> Plan -> Build -> Check -> Integrate -> Done`
- ASP.NET Core Server + Orleans grains 控制平面
- TypeScript Runner 执行 agent、git、workspace 动作
- React Web UI：看板、Issue 详情、活动、日志、设置、归档、Epic
- `mo` CLI：server、runner、project、issue、config、skills、update 等命令
- OpenSpec 风格产物目录：`openspec/changes/`

Explore 不是 Mohist runtime 的内置功能。需求澄清和探索由外部 coder-agent skill 处理，例如 `mohist-explore`。

## 仓库结构

```text
packages/server/   ASP.NET Core + Orleans backend
packages/runner/   TypeScript runner runtime
packages/web/      React Web UI
packages/cli/      mo CLI
docs/              补充文档
design/            架构与设计说明
openspec/          Change 产物与归档
```

## 环境要求

- .NET SDK 10.0+
- Node.js 18+
- npm 9+
- `opencode` CLI 在 `PATH` 中可用

## 从源码运行

```bash
# 安装依赖
npm install

# 构建 .NET 项目
npm run build

# 启动 Server
npm run dev:server

# 另一个终端启动 Web UI 开发服务器
npm run dev:web
```

默认访问地址：`http://localhost:3456`

## 常用命令

```bash
# 创建项目并切换到当前项目
mo project create my-app --path .
mo use my-app

# 创建并启动一个 Issue workflow
mo issue create "Add search feature" --body "Users need search"
mo issue start 1

# 审批等待中的 gate
mo issue approve 1

# 查看状态和日志
mo status
mo issue show 1
mo issue logs 1
```

当前 CLI 顶层命令包括：

- `mo status`
- `mo logs`
- `mo use`
- `mo server ...`
- `mo runner ...`
- `mo install ...`
- `mo update ...`
- `mo skills ...`
- `mo project ...`
- `mo issue ...`
- `mo config ...`

## Web UI

Web UI 当前覆盖的主要能力：

- 看板首页
- Issue 详情与改动查看
- coder session 查看
- 活动监控
- 设置页
- 日志页
- 归档页
- Epic 列表与详情

## 工作流与产物

工作流形态：

```text
Draft -> Plan -> Build -> Check -> Integrate -> Done
           ^                  |           |
           |                  v           v
        Backlog             Build       Build
                          (rejected)  (integrate failed)
```

典型产物目录：

```text
openspec/changes/{issue-number}-{slug}/
  proposal.md
  design.md
  specs/
  tasks.json
  self-review.md
  review.md
```

## 开发与验证

```bash
# 构建与测试
npm run build
npm test

# 单独构建 Web UI
npm run build:web

# 单独构建 Runner
npm run build:runner
```

更多协作说明见 `docs/CONTRIBUTING.md`。

## 说明

- 架构边界以 `design/architecture.md` 为准。
- 仓库中的历史 PRD、talk notes 和 explore 草稿已移除，避免把旧设计误读为当前实现。

## License

MIT
