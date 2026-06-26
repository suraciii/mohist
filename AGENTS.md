# Agent Instructions

## Project Overview

Mohist 是一个面向个人开发者的本地优先软件生产系统，通过可自定义的工作流推进 issue，扩大软件产出的规模。

**本项目正处在积极开发过程中，无需考虑版本兼容**

## 技术栈

- **server** —— ASP.NET Core + Orleans，.NET 11（`packages/server/`）
- **runner** —— TypeScript，Node（`packages/runner/`）
- **web** —— React 19 + Vite + TanStack Query（`packages/web/`）
- **cli** —— .NET，命令名 `mo`（`packages/cli/`）

## 仓库布局

```
packages/
  server/    控制平面（ASP.NET Core + Orleans）
  runner/    执行平面（TypeScript）
  web/       Web UI（React）
  cli/       mo CLI
docs/        用户文档
design/      架构、领域分析、约定（开发向）
openspec/    工作流产出的变更产物
```

## 构建与运行

```bash
npm run build          # 构建 .NET（dotnet build Mohist.sln）
npm run dev            # 启动 server + Web UI（并发）
npm run dev:runner     # 另一个终端启动 runner
```

重启受管理服务务必用 `mo update`（不要手动 `dotnet run`，会触发 runner id 漂移导致 workflow sticky assignment 失配）：

```bash
mo update server       # 重建并以 systemd 受管理方式重启 server
mo update runner       # 同上，重启 runner
```

## 测试与类型检查

改完代码务必跑对应 typecheck + test：

```bash
# server（C# 靠 TreatWarningsAsErrors 当 lint）
npm test

# web
npm run typecheck -w packages/web
npm run test:run   -w packages/web

# runner
npm run typecheck  -w packages/runner
npm test           -w packages/runner
```

## 改代码前必读

- **边界与放置规则**：[`design/architecture.md`](design/architecture.md) —— 什么放 Server / Runner / CLI，执行事实与状态裁判分离。
- **领域分解**：[`design/domain-analysis.md`](design/domain-analysis.md) —— 核心域（Workflow）与支撑子域，判断改动落在哪个域。
- **约定**：[`design/conventions.md`](design/conventions.md)。

## 设计原则

* 数据模型应该尽可能地简洁，只包括必要的属性

## 测试原则

* **绝对禁止**直接使用真实的外部系统（API、操作系统、外部进程等），应当使用Fake
* 测试的运行时间应该足够快，需要保证测试能在合理的时间里完成
