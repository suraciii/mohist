# Agent Instructions

## Project Overview

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

mo update # 更新运行版本
```

## 构建输入与工作区

- 在新的工作区先显式运行 `npm ci`；构建和测试命令不得隐式安装或改写依赖锁文件。
- 改动 JavaScript 依赖时，使用对应的 npm workspace 命令更新清单与锁文件；不要靠构建过程补齐依赖。
- 验证命令完成后不得留下 Git 可见的改动。若命令会产生改动，应修复命令或其输入，而不是在验证后静默清理。

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

- **产品愿景**：[`docs/vision.md`](docs/vision.md) —— 产品要去哪里、三条原则；判断改动是否对齐方向。
- **边界与放置规则**：[`design/architecture.md`](design/architecture.md) —— 什么放 Server / Runner / CLI，执行事实与状态裁判分离。
- **领域分解**：[`design/domain-analysis.md`](design/domain-analysis.md) —— 核心域（Workflow）与支撑子域，判断改动落在哪个域。
- **约定**：[`design/conventions.md`](design/conventions.md)。
- **测试**：[`design/testing.md`](design/testing.md) —— spec/unit 两条轨道、外部依赖与时间依赖的硬约束、各端 fake 入口。

## 设计原则

* 模型应该尽可能地简洁，只包括必要的属性

## 注释原则

代码默认不写注释；优先用清晰命名、类型、函数边界和领域模型让代码自解释。只有当代码本身无法表达“为什么这样做”时才写注释，例如外部系统限制、关键不变量、反直觉选择或真实风险。若注释在解释代码“做什么/怎么做”，先重构代码，让注释消失。

注释不引用设计文档、issue、spec 或任务编号——文档会改名移动，引用必然腐烂；历史归 git log，不归注释。设计文档已有的内容不在注释里重复。

## Spec 先行与文档分工

文档分两层，每层是一种 spec，都先于实现——**先确定方案落到文档，再去实现**。spec 只有这两层：issue 交付产生的需求增量直接更新 `docs/` 或 `design/` 的对应篇，不另建 spec 文件。

* **产品 spec（`docs/`）** —— 产品该满足什么：用户需求、命令面、场景、心智模型、责任边界。用**产品语言 + 领域语言**，不用技术语言。面向使用者，假设读者不读代码。
* **设计 spec（`design/`）** —— 系统该怎么实现：架构边界、数据模型、接口、技术选型与取舍。可以用**产品语言 + 领域语言 + 技术语言**。

两层都遵循 spec 先行：实装由 issue 追赶 spec，而非 spec 跟着实装走。文档里出现尚未实装的能力是正常的，由对应 issue 推进落地；落地后无需改文档（它本就描述目标）。当某篇文档与当前代码有显著差距时，在文内单列差距小节说明现状与对应 issue——**正文是 spec，差距是脚注**。

**三种语言怎么分**：
- **产品语言**——用户视角的词（审批门、订阅、响应提示词、Agent、issue 完成了）。两层都可用。
- **领域语言**——领域模型术语（Approval、WorkflowRun、Stage、Subscription）。两层都可用。
- **技术语言**——实现术语（grain、handler、dispatch、落库、反查、字段名、源码路径、API）。**只许 `design/` 用，`docs/` 禁止**。

两层各自的 WIP/未实装标注惯例见 [`docs/README.md`](docs/README.md) 与 [`design/README.md`](design/README.md)。

spec 定稿后规划实施 issues 时，把 spec 作为已有需求材料交给 `mohist-explore` skill（`mo skills get mohist-explore`）切分——切分必须过它的 Scope 门槛：每个 issue 的独立交付价值能用一句话说清。

## 测试原则

详见 [`design/testing.md`](design/testing.md)。要点：

* **区分 spec 与 unit**：spec 验证产品行为（高集成），unit 验证模块/类（低集成）。命名与放置要一致表达验证对象。
* **禁止真实外部依赖**：不得触碰真实网络/进程/git/DB/系统服务，全部走 fake（DI 注入 / factory hook / mock）。
* **禁止真实时间**：时间逻辑必须可注入（C# `TimeProvider` / TS `vi.useFakeTimers`），不得走墙钟；不得用 `while(now<deadline)` 或 `elapsed < N` 做断言。
* **不得 flaky**：不得依赖顺序、时间戳种子、未恢复的 stub；不得用 `it.skip` 掩盖 flaky。
* **简洁、无冗余、读得出 spec**：setup 抽共享 helper，迁移/回归完成后删旧文件，禁止新旧并存。
* **足够快**：unit < 50ms，spec < 500ms；browser 单独跑，不进默认 `npm test`。
