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

spec 定稿后规划实施 issues 时，把 spec 作为已有需求材料交给 `mohist-explore` skill（`mo skill view mohist-explore`）切分——切分必须过它的 Scope 门槛：每个 issue 的独立交付价值能用一句话说清。

## 测试原则

详见 [`design/testing.md`](design/testing.md)。要点：

* **区分 spec 与 unit**：spec 验证产品行为（高集成），unit 验证模块/类（低集成）。命名与放置要一致表达验证对象。
* **禁止真实外部依赖**：不得触碰真实网络/进程/git/DB/系统服务，全部走 fake（DI 注入 / factory hook / mock）。
* **禁止真实时间**：时间逻辑必须可注入（C# `TimeProvider` / TS `vi.useFakeTimers`），不得走墙钟；不得用 `while(now<deadline)` 或 `elapsed < N` 做断言。
* **不得 flaky**：不得依赖顺序、时间戳种子、未恢复的 stub；不得用 `it.skip` 掩盖 flaky。
* **简洁、无冗余、读得出 spec**：setup 抽共享 helper，迁移/回归完成后删旧文件，禁止新旧并存。
* **足够快（由 test-duration 守卫强制执行）**：群体硬约束 unit p95 ≤ 50ms、spec p95 ≤ 500ms（“绝大多数快”）；逐测绝对硬上限 unit/arch ≤ 500ms、spec ≤ 5s（design/testing.md 的 hard cap），超过必须进入版本控制的逐项 allowlist（含 identity/observed/reason/owner/移除期限），否则失败；全量 suite 5 分钟 hard deadline。p95 超限不可用 allowlist 免除。Architecture 测试为结构类，只受 500ms 绝对上限约束，不设 p95。详见 [design/testing.md](design/testing.md)。browser 单独跑，不进默认 `npm test`，也不进守卫。

### 测试时长守卫

`npm run test:budget`（`scripts/test-duration/guard.ts`）在本地与 CI 以同一机制强制执行时长策略：

* 全量 5 分钟 hard deadline + 每 track hard deadline，跨平台用 Node 子进程 kill，不依赖 Linux `timeout`。
* 解析真实报告：vitest JSON 与 xUnit TRX。
* 两道硬约束（都为失败，不是 warning）：群体 unit p95 ≤ 50ms / spec p95 ≤ 500ms；逐测绝对上限 unit/arch ≤ 500ms、spec ≤ 5s（design/testing.md hard cap）。Architecture 测试为结构类，只受 500ms 绝对上限约束，不设 p95。
* 超逐测绝对上限的测试必须在 `test-duration.config.jsonc` 逐项 allowlist 中，含 identity/observed/reason/owner/removal deadline；过期则失败。p95 超限不可用 allowlist 免除。
* MTP 忽略 `dotnet test --filter`：focused 流程直接跑编译后的 xUnit v3 apphost（见下）。

默认只跑受控 track（enforce=true，含 server-unit/server-spec/server-arch）；`--all` 加入 deadline-governed、baseline-pending track。未 baseline 的 track 显式 `status: baseline-pending`，只受 deadline 管控。

### C# focused test

C# 测试项目使用 Microsoft Testing Platform + xUnit v3。不要把 VSTest
筛选器当作 focused test：

* 禁止使用 `dotnet test <csproj> --filter "FullyQualifiedName~..."`。当前会报告 `MTP0001`，说明 `VSTestTestCaseFilter` 被忽略，并可能执行整个测试程序集。
* `dotnet test <csproj> --no-restore --no-build -- -class <FQCN>` 的 pass-through 当前不可靠：MTP 会把 `-class` 变成 `--class` 并报告未知选项。
* 新 worktree 先显式运行 `npm ci`。缺少 `obj/project.assets.json` 时先显式运行 `dotnet restore <csproj>`；之后 build/test 都显式使用 `--no-restore`，已 build 的 focused test 使用 `--no-build` 的 dotnet 命令或直接使用编译产物，禁止隐式安装、改写 lock。

正确的 focused class 命令是直接运行编译后的 xUnit v3 apphost。先用该 apphost 的 `--help` 确认存在 `-class`，再执行：

```bash
dotnet build packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -list classes -noColor -noLogo \
  -class Mohist.Cli.Tests.Skills.SkillsContentTests
packages/cli/tests/Mohist.Cli.Tests/bin/Debug/net11.0/Mohist.Cli.Tests \
  -noColor -noLogo -class Mohist.Cli.Tests.Skills.SkillsContentTests
```

验收时必须确认 discovery 列出目标 FQCN，执行摘要为 `Total: N` 且 `N > 0`；`Total: 0` 不是通过证据。上例本次实测只列出 `Mohist.Cli.Tests.Skills.SkillsContentTests`，执行为 `Total: 24`。

聚焦跑流程已封装进守卫，可避免手拼 apphost 路径，且绝不退回 `dotnet --filter`：`npm run test:budget -- focused <csproj> <FQCN>`（先 `-list classes` 校验 FQCN 存在，再 `apphost -class` 执行）。

## Broken-CI 处置协议

CI 挂了（hang / flake / red）禁止无限 rerun/poll。先 diagnostic-first——看失败测试与日志、对照已知 flake，定位根因后再按决策树三选一：

```text
CI 阻塞
├─ (a) 修 —— 根因在域内且修复便宜：修代码/测试，验证后正常合并
├─ (b) 带安全网绕过 —— 根因不在域内 / 修复贵 / 已知 flake / CI 基础设施问题：
│     1. 跑 scripts/pre-merge-check.sh（#314 的本地快速 gate），必须全绿
│     2. 临时放宽分支保护 → 合并 → 精确恢复分支保护（重新启用被禁的检查）
└─ (c) 升级 goal_blocked —— 需要用户裁决 / 根因在外部：带证据升级，等用户
```

- **硬时间盒**：同一 CI 阻塞**连续 2 个 goal turn** 仍在 rerun/poll → **强制**转入三选一，禁止继续 rerun/poll。
- **沉没成本警戒线**：某子问题花的 turn 超过实现本身 → 停，重审策略（换分支或升级）。
- **绕过前置**：绕过前必须跑 `scripts/pre-merge-check.sh` 且全绿——快速套件（server build、ArchTests 含 spec-file-size 门槛、UnitTests、mohist-slack vitest）任一步失败即不允许绕过，回到 (a) 或 (c)。

**真实教训（本 milestone 实测）**：既有 flake——`IssueCompositeLifecycleGrainSpecs`、`GitHubWriteBackSpecs.Cancelled_WithReason`、`EventDispatcherImmediateTriggerSpecs`、`AgentJobSubagentTerminalCallbackSpecs`——间歇命中 CI（约 20-40%/run），与本 milestone 改动无关；#312 修了 hang；PR #337 在修 GitHubWriteBack flake。这正是硬时间盒 + 三选一的来由：别在 flake 上无限 rerun。

## 角色边界：灰区规则 + 破例日志

main agent 只协调：不读不写源码，实现一律委派 agent。边界分三类：

| 类别 | 内容 |
|---|---|
| **允许**（main agent 直接做） | git/gh 操作、issue/PR/分支保护管理、读 spec 做规划、只读诊断、跑验证 |
| **灰区**（默认委派） | 改 `docs/`、测试配置 JSON（`test-duration.config.jsonc`、`spec-file-size-baseline.json` 等）、文档冲突处理 |
| **禁止**（一律委派） | 源码（`packages/**/*.{cs,ts,…}`）与测试逻辑 |

- **破例日志**：灰区动作默认委派；真要自己做，**当场**在 turn 报告里显式标注「破例：<动作>，理由：<原因>」。破例是显式、单次的，不积累成惯例。
- **轻量委派路径**：灰区小改动必须委派得起——herdr worktree + agent 秒级启动（`herdr worktree create` → `herdr agent prompt`），派单遵守 [`design/dispatch-template.md`](design/dispatch-template.md) 三条硬规则（model fallback 链 + 探活、测试命令 timeout、完成定义）。

**真实教训（本会话）**：Slack milestone 压力下，主 agent 曾自己改 `design/slack.md` + spec-file-size baseline JSON，边界被侵蚀。根因是委派小改动的开销大于直接改——破例日志让破例留痕，轻量委派路径把委派成本降下来，两者配套消除破例诱因。

## Agent dispatch 模板

向外部 agent（herdr / pi）派发本仓库开发任务时，派单 prompt 必须套用 [`design/dispatch-template.md`](design/dispatch-template.md) 的三条硬规则：

1. **model fallback 链**：廉价默认 `opencode-go/deepseek-v4-flash` → 廉价备选（`minimax/MiniMax-M3` 等，异 provider）→ 宝贵 `zai-coding-cn/glm-5.2`（用时显式标注"宝贵智力资源"）。模型名必须带 provider 前缀（裸名在多个 provider 间歧义，pi 启动即报错）；派单前先探活（`timeout 15s pi --no-session --model <m> -p 'ok'`），探活失败直接跳下一档，不赌 broken provider。
2. **测试命令强制 timeout**：所有会跑测试的命令必带 `timeout -k 10s <N>s …`（unit/arch `120s`、spec `180s`、全量套件 `480s`），避免被 hang 拖死。
3. **完成定义**：build 过 + 相关测试过 + PR 已开，缺一不算完成；完成报告附三件证据（命令 + 退出码/结果摘要 + PR 链接）。
