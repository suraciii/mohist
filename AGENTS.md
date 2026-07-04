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
- **测试**：[`design/testing.md`](design/testing.md) —— spec/unit 两条轨道、外部依赖与时间依赖的硬约束、各端 fake 入口。

## 设计原则

* 数据模型应该尽可能地简洁，只包括必要的属性

## 文档分工原则

文档分两层，每层有自己的语言边界：

* **产品文档（`docs/`）** 用**产品语言 + 领域语言**，不用技术语言。写用户需求、心智模型、用户怎么用、场景、责任边界。面向使用者，假设读者不读代码。
* **设计文档（`design/`）** 可以用**产品语言 + 领域语言 + 技术语言**。写架构边界、数据模型、接口、handler、存储、技术选型与取舍。
* **三种语言怎么分**：
  - **产品语言**——用户视角的词（审批门、订阅、响应提示词、Agent、issue 完成了）。两层都可用。
  - **领域语言**——领域模型术语（Approval、WorkflowRun、Stage、Subscription）。两层都可用。
  - **技术语言**——实现术语（grain、handler、dispatch、落库、反查、字段名、源码路径、API）。**只许 `design/` 用，`docs/` 禁止**。
* 两层各自的 WIP/未实装标注惯例见 [`docs/README.md`](docs/README.md) 与 [`design/README.md`](design/README.md)。

## 测试原则

详见 [`design/testing.md`](design/testing.md)。要点：

* **区分 spec 与 unit**：spec 验证产品行为（高集成），unit 验证模块/类（低集成）。命名与放置要一致表达验证对象。
* **禁止真实外部依赖**：不得触碰真实网络/进程/git/DB/系统服务，全部走 fake（DI 注入 / factory hook / mock）。
* **禁止真实时间**：时间逻辑必须可注入（C# `TimeProvider` / TS `vi.useFakeTimers`），不得走墙钟；不得用 `while(now<deadline)` 或 `elapsed < N` 做断言。
* **不得 flaky**：不得依赖顺序、时间戳种子、未恢复的 stub；不得用 `it.skip` 掩盖 flaky。
* **简洁、无冗余、读得出 spec**：setup 抽共享 helper，迁移/回归完成后删旧文件，禁止新旧并存。
* **足够快**：unit < 50ms，spec < 500ms；e2e/a11y 单独跑，不进默认 `npm test`。
