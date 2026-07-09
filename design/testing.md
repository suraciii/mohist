# Testing

所有包（`server` / `runner` / `web` / `cli`）的测试遵守本文。

## 轨道：spec 与 unit

写测试先回答："验证对象是产品还是模块？"

| 轨道 | 验证对象 | 集成程度 | 放置 |
|------|----------|----------|------|
| **Spec** | 产品行为 / 用户流程 | 高：从产品入口（HTTP route、CLI 命令、组件渲染）进，跨多个真实协作者 | 贴近产品面 |
| **Unit** | 单个模块 / 类 / 函数 | 低：只构造被测对象，协作者全 fake | 贴近被测代码 |

架构规则（ArchTests）自成第三类：验证结构而非行为，依赖面只有 production 程序集，单独放置。

**轨道由结构表达**——命名 + 目录 +（必要时）项目边界，不靠运行时 trait：

| 端 | unit | spec |
|----|------|------|
| C#（server / cli） | `*Tests.cs`，UnitTests 项目 | `*Specs.cs`，SpecTests 按 context 分目录 |
| runner | `*.test.ts`，贴 `src/` | `*.spec.ts` |
| web（例外） | `.test` = `src/` collocated | `.spec` = `tests/` 跨切面；后缀编码**位置**而非轨道 |

- C# `Speed` trait 是 fixture 成本标签，用于 `--filter` 与并行控制，不是轨道标签。
- **一个文件只测一个被测对象**：文件名、目录、describe 标题三者一致表达"验证的是什么"。一个文件同时测 widget + page + model 是错位，拆开。

## 硬约束

四条，违反即视为不合格测试，审查打回。

### 1. 不碰真实外部环境

判别：**断网、无 git / node / opencode、空 `$HOME` 的容器里也必须全绿。**

禁真实网络、进程、git / shell / agent 二进制、数据库文件、系统服务、`process.env` / `Environment` 直读。全部走注入的 fake（见速查表）。允许受控临时目录 + 测试自清理。

### 2. 不碰真实时间

判别：**fake 时钟不推进，时间逻辑就不发生。**

- logic-under-test 不得以 `DateTime.UtcNow` / `Date.now()` / `new Date()` 为权威时间——注入 `TimeProvider`（C#）或 `now` / fake timers（TS）。
- 测试不得墙钟等待：`while (now < deadline)` 轮询、`Task.Delay` / `setTimeout` / `Sleep` "等一下"、`elapsed < N` 断言，全部禁止。用可 await 的信号（`TaskCompletionSource`）或 fake timer 推进。
- `waitFor` 类真墙钟轮询只在驱动真实异步收敛时用，timeout 尽量小。

### 3. 确定性（不 flaky）

判别：**任意顺序、任意并行、重跑一千次结果一致。**

- fixture 时间戳用固定常量（如 `2026-01-01T00:00:00Z`），相对时间断言基于 fake 时钟。`Guid.NewGuid()` 只用于"唯一即可、不被断言"的占位。
- 不依赖数组顺序、`Object.keys` 顺序、调度顺序。
- 并行安全：会并行的 silo fixture 必须随机端口注入（`TestClusterPortAllocator`），禁止硬编码 11111/30000；操作进程级全局资源的测试类留在串行 collection。
- stub 恢复：vitest 已配置自动恢复 mock / global / env stub；fake timers 仍需测试自己 `afterEach` 关。
- flaky 测试（"我机器上能过 / CI 偶发挂"）要么改成确定信号，要么删。**禁止用 `it.skip` 掩盖**。

### 4. 快且简洁

判别：**读测试体 = 读产品规约；慢通常是 fake 不到位或粒度错。**

| 轨道 | 单测试 | 文件 |
|------|--------|------|
| Unit | < 50ms | < 300 LOC |
| Spec | < 500ms，硬上限 5s；collection ≤ 2min | < 800 LOC（C# 24KB 由 ArchTest 强制） |
| E2E / a11y | 单独 `npm run test:e2e` / `test:a11y`，**不进默认 `npm test`** | |

- 冗长 setup 抽共享 helper；同一产品能力不在两个文件各写一遍；迁移式拆分完成后删旧文件，禁新旧并存。
- 一次性回归 / 迁移测试：一旦等价常规覆盖存在即删，issue 号写进常规测试注释。
- 能下沉到 unit 的断言不要用全栈 fixture 扛。

## 机器守卫

能机器化的规则应下沉为守卫，**报错文案写明替代入口**（先例：env 守卫的 analyzer 报错直接指向 `IEnvironmentVariableProvider`）。守卫落地一条，本文删一条。

已有：

- **ArchTests**：分层依赖方向、spec 文件命名 / 24KB 预算 / namespace / public。
- **`EnvironmentAbstractions.BannedApiAnalyzer`**：编译期禁直读 env，ArchTest backstop 保证 analyzer 挂在每个生产 csproj。
- **vitest 配置**：`restoreMocks` / `unstubGlobals` / `unstubEnvs` 自动恢复；projects 按后缀分 node / jsdom 环境。

待建（转 issue 后此处只留链接）：

- C# BannedSymbols：墙钟（`DateTime.UtcNow` 等）、`Task.Delay` / `Thread.Sleep`、裸 `new HttpClient()`、`Process.Start`、测试内 `Database.Migrate()`（`*MigrationSpecs` 豁免）。
- UnitTests 禁引重型 fixture（`WebApplicationFactory` / `Orleans.TestingHost`）的 csproj backstop。
- ESLint：测试内禁 `child_process` / 真 `@microsoft/signalr` import，message 指向 fake 入口。
- MSW `onUnhandledRequest: 'error'` 全量确认。

## fake 入口速查

公共 fake 放 `Support/`（C#）/ `tests/support/`（TS）。守卫报错文案覆盖后本表退役。

| 依赖 | server | runner | web | cli |
|------|--------|--------|-----|-----|
| 时间 | `FakeTimeProvider` | `vi.useFakeTimers` | 同 runner，自开自关 | 缺 seam（差距） |
| HTTP | `WebApplicationFactory` + `TestServer` | `vi.stubGlobal('fetch')` | MSW 共享 server | `RecordingHttpHandler` |
| SignalR | `RecordingRunnerHubContext` | `vi.mock('@microsoft/signalr')` | 同 runner | — |
| 进程 | — | `setAcpProcessFactoryForTest` / `vi.spyOn(system.process)` | — | `FakeCommandExecutor` / `ScriptedCommandExecutor` |
| DB | in-memory SQLite，schema 从 `MigratedSqliteTemplate.CopyTo` 克隆（不跑 `Migrate()`） | — | — | fake `IOtelQueryExecutor` |
| Grain | `InProcessTestCluster`、`ControllableReminderTable` | — | — | — |
| 渲染 | — | — | `customRender`（`tests/test-utils.tsx`，已包 QueryClient + Router + Project） | — |
| 文件 / 数据 | `Support/TestData/*` 固定常量工厂 | `tests/support/*`（workspace、fake-acp） | `tests/support/*` | `FakeFileSystem` |

## 审查清单

提 PR 前自查，审查时对照：

1. 这条测试是 spec 还是 unit？文件名 / 目录 / describe 一致吗？
2. 它触碰真实网络 / 进程 / git / DB / 系统服务了吗？
3. 它用了真实墙钟吗？（`UtcNow` / `Date.now()` / 墙钟轮询 / `Delay` 等待 / `elapsed < N` 断言）
4. 它会 flaky 吗？顺序依赖 / 时间戳种子 / 未恢复的 fake timers？
5. 有没有另一个文件测同一件事？是不是迁移残留？
6. setup 能不能抽共享 helper？fixture 成本是不是过度？
7. 读测试体能看出产品规约吗？还是淹没在 mock 装配里？

## 差距脚注

正文是 spec，以下是现状差距，收敛后删：

- server：纯 unit 文件仍混在 SpecTests，逐 context 迁入 UnitTests；runner：存量 `*.spec.ts` 逐文件判定中。
- "UnitTests 禁重型 fixture"目前纯靠 review，守卫见上节待建清单。
- cli 时间逻辑（`ServiceReadinessProbe` 等）缺 `TimeProvider` seam。
