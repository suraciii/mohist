---
purpose: "测试分类、编写与运行的统一约定。区分 spec 与 unit，约束外部依赖与时间依赖。"
include:
  - "Spec test 与 unit test 的边界和放置规则。"
  - "外部环境、时间、确定性、速度的硬性要求。"
  - "C# / TypeScript 各端的 fake 与注入入口。"
exclude:
  - "具体测试用例的业务含义。"
  - "单个 fixture 的实现细节。"
  - "CI 流水线配置。"
style:
  - "规则 + 反例优先，少用散文。"
  - "可被 lint/审查直接对照的硬约束。"
---

# Testing

本文是 Mohist 的测试约定。所有包（`server` / `runner` / `web` / `cli`）的测试都遵守这里。

## 两条测试轨道

Mohist 的测试分两条轨道，写之前先回答："这条测试属于哪条轨道？"

| 轨道 | 验证对象 | 集成程度 | 放置 | 命名 |
|------|----------|----------|------|------|
| **Spec** | 产品行为 / 用户流程 | 高（端到端地穿过多个模块） | 贴近被验证的产品面 | `*Specs`（C#）/ `*.spec.ts`（TS） |
| **Unit** | 单个模块 / 类 / 函数 | 低（只 `new` 被测对象） | 贴近被测代码 | `*Tests`（C#）/ `*.test.ts`（TS） |

判断规则：

- Spec 回答"这个产品能力对不对"。它从产品入口（HTTP route、CLI 命令、组件渲染、workflow 端到端）进，跨多个真实协作者。
- Unit 回答"这个模块的行为对不对"。它只构造被测对象，协作者全部 fake。
- 不要用 fixture 重量级程度给测试分类。**用"验证对象是产品还是模块"给测试分类**。一个文件名是 `*Specs` 却只测一个纯 parser，是错位的。
- 一个 spec 文件不要塞多个产品面。`SessionPage.test.tsx` 同时测 widget + page + model 是错位的。

## 结构承担轨道，trait 承担分级

先定轨道（Spec / Unit / Architecture，依据是"验证对象是产品、模块还是结构"），再用结构把轨道落实下来——**不能只靠运行时 trait**。三个手段，成本与强度递增，按需叠加：

| 手段 | 表达 | 何时用 |
|------|------|--------|
| 命名 | 文件属哪条轨道 | 永远，必做 |
| 目录 | 轨道 + 验证对象的集成层次 | unit/spec 一多就分目录 |
| 项目（csproj） | 轨道之间的依赖方向 | 只在想把"unit 不得依赖重型 fixture"变编译不变量时 |

- **命名二分，一个文件只属一条轨道**：

  | 栈 | unit | spec |
  |----|------|------|
  | C# | `*Tests.cs` | `*Specs.cs` |
  | TS | `*.test.ts` | `*.spec.ts` |

- **放置表达验证对象**：unit 贴近被测模块（web colocate 到 `src/`，其余按模块归集）；spec 贴近产品面（按 bounded context，再按集成层次 `Api/` / `Domain/` / `Grain/` / `Services/`）。
- **架构规则自成一类，单独放置**：它不验证行为，依赖面只有 production 程序集，与行为测试不相交。塞进 `Specs/` 是错位。
- **trait 是 fixture 成本标签，不是轨道标签**：C# `Speed=Unit|Grain|Service|Integration` 用于运行时选择（`--filter Speed=Unit`）与并行控制。一个 `Speed=Unit` 的文件可能是便宜的 spec（纯 parser 的产品契约），也可能是 unit——**轨道由结构表达，不由 trait 决定**。

反例：

- 靠 `Speed` trait 区分 unit/spec，命名和目录却混在一起 → trait 扛了结构的活，要 grep 才看得见轨道。
- 文件叫 `*Specs.cs`、标 `Speed=Unit`，验证的却是一个模块的内部行为（不是产品契约）→ 轨道错位，它是 unit，改名 `*Tests.cs` 并移出 spec 目录。
- 把 arch 规则文件塞进 `Specs/`，和带 fixture 的行为测试同处 → 依赖面混淆。

**落地现状**（spec 是目标，以下是差距脚注，逐批收敛后删本注记）：

- **server**：三项目骨架已建——`SpecTests`（已改名、在 sln）、`ArchTests`（已实装、在 sln）、`UnitTests`（骨架已建）。`Speed=Unit` trait 在历史上被当作"相对快的测试"速档滥用，真正的纯 unit 文件仍混在 SpecTests 里，需逐 context 判定后迁入 `UnitTests`。迁移分批推进，从 `Foundation` 起逐 context 迁。
- **"禁依赖"靠约定，不靠编译**：`UnitTests` 不引用 `WebApplicationFactory` / `Orleans.TestingHost` 是当前事实，但没有任何 analyzer / BannedApi / ArchTest 守卫，纯靠 review。要把它变真约束，待加 ArchTest 守卫或 BannedApiAnalyzer。
- **cli**：生产 + 测试项目已进 sln，命名二分 `*Tests.cs` / `*Specs.cs`。
- **runner**：引入 `*.test.ts` unit 轨道（首批 6 个纯函数已迁），剩余 `*.spec.ts` 逐文件判定中。
- **web**：顶层 page 级测试 `*.test.tsx` → `*.spec.tsx` 的 rename 未按字面执行；实际落地的约定是 `.test` = src/ collocated、`.spec` = `tests/` 跨切面，后缀编码位置而非 unit/spec 语义。`SessionPage.test.tsx` 多对象反例已拆分。

## 硬性原则

下面六条是硬约束，违反即视为不合格测试，审查应打回。

### 1. 不依赖真实外部环境

测试不得触碰真实网络、真实进程、真实系统服务、真实 git/shell/agent 二进制、真实数据库引擎（含 SQLite 文件）、真实用户家目录之外的 fs 状态。

允许：

- 受控临时目录（`Path.GetTempPath()` / `tmpdir()` / `mkdtemp`）+ 测试自清理。
- 通过 DI / 构造参数 / factory hook 注入的 fake。
- TestServer / `WebApplicationFactory` / jsdom / mocked SignalR / mocked `fetch` / mocked `child_process`。

禁止（含但不限于）：

| 禁止 | 用什么代替 |
|------|-----------|
| `Process.Start` / `execFile` / `spawnSync` 真 git / node / opencode / `systemctl` / `dotnet` | `FakeCommandExecutor`（cli）/ `setXxxGitRunnerForTest`（runner）/ factory 注入 |
| 真 `SqliteConnection` 到磁盘文件（含 WAL/SHM sidecar） | in-memory shared-cache SQLite（`Mode=Memory;Cache=Shared` + keeper 连接），或 fake query 执行器 |
| 真 `fetch` / `HttpClient` 到 `localhost:*` 真绑定 | `RecordingHttpHandler`（cli）/ `vi.stubGlobal('fetch')` / `vi.mock` / MSW |
| 真 `@microsoft/signalr` WebSocket | `vi.mock("@microsoft/signalr", ...)` |
| `Environment.MachineName` / `Environment.UserName` / 真 `process.env.X`（除测试自己 set+restore） | `MockEnvironmentVariableProvider` / `vi.stubEnv` |
| 硬编码他人机器路径（如 `/home/xxx/.cache/...`） | 仅读 env，缺省报错而非落硬路径 |

判别一句话：**测试在断网、无 git、无 node、无 opencode、空 `$HOME` 的容器里也必须全绿。**

### 2. 不依赖真实时间

所有时间相关逻辑必须能被测试注入的时间源驱动，不允许走系统墙钟。

| 栈 | 注入入口 | 测试端用法 |
|----|----------|-----------|
| C# | `TimeProvider`（.NET 8+） | `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`，` Advance(TimeSpan)` / `SetUtcNow(DateTimeOffset)` |
| TS | `vi.useFakeTimers()` + `vi.setSystemTime()` + `vi.advanceTimersByTimeAsync()` | 或注入 `now: () => number` 参数（runner `WorkspaceRegistry` 范式） |

禁止：

- 在 logic-under-test 里直接 `DateTime.UtcNow` / `DateTimeOffset.UtcNow` / `Date.now()` / `new Date()` 作为权威时间。要么通过注入的 `TimeProvider`，要么接受一个 `now` / `clock` 参数。
- 在测试里用 `while (DateTime.UtcNow < deadline) { ... }` 这类墙钟轮询等待异步副作用。用 `TaskCompletionSource` 信号、`ControllableReminderTable` 模式，或先 `Advance` 时间再断言。
- 用 `Task.Delay(ms)` / `setTimeout(ms)` / `Thread.Sleep(ms)` "等一下" 让异步副作用 settle。要么把副作用做成可 await 的信号，要么用 fake timer 推进。
- 用 `elapsed < N ms` 这类真实墙钟上界做断言（`expect(elapsed).toBeLessThan(1000)`、`elapsed >= 4500 && elapsed < 10000`）。这是 flaky 的标准配方。

允许但应少用：`vi.waitFor` / Testing Library `waitFor`——它本质是真实墙钟轮询，只在驱动真实异步收敛时用，且 timeout 要尽量小。

### 3. 不得有 flaky 测试

flaky 的常见来源，逐一排除：

- 真实时间等待（见上）。
- 真实进程 / 网络时序（见原则 1）。
- 顺序依赖：测试依赖数组顺序、`Object.keys` 顺序、调度顺序、collection 内执行顺序。
- 时间戳种子：fixture 用 `DateTimeOffset.UtcNow` 造数据，断言又依赖相对时间（"2m ago" / "yesterday"）。**fixture 时间戳必须固定常量**（如 `2026-01-01T00:00:00Z`），相对时间断言必须基于 fake 时钟。
- `Guid.NewGuid()` 用在会被断言的位置。`NewGuid` 只能用于"只要唯一就行、不会被读"的占位（数据库名、临时目录名）。
- 跨测试状态泄漏：`vi.setSystemTime` / `vi.useFakeTimers` / `process.env` / `globalThis.fetch` / `navigator.clipboard` 必须在 `afterEach` 恢复，恢复写在 `afterEach`，**不要写在测试体末尾**——断言抛错就跳过了。
- 静默吞异常：`try { ... } catch { }` 包住 schema 迁移 / setup，失败被吞，下游测试得到错误前提。

一条测试如果"在我机器上能过 / 在 CI 偶发挂 / 重跑又过了"，按 flaky 处理：找到非确定性来源，要么改成确定信号，要么删掉。**不允许用 `it.skip` 把 flaky 测试藏起来**，除非同文件里已有等价的、用 fake 时间/信号的版本。

### 4. 足够快

集成不是慢的借口，慢通常是 fake 不到位或粒度错。

- Spec 文件要能一眼看出"它在验证哪个产品能力"。读测试 = 读产品 spec。
- 每条 spec 测一个产品切片，不要在一个文件里堆十几个不相关场景。
- Fixture 启动成本与文件数相乘。能下沉到 unit 的，不要用 `MohistIntegrationFixture`（启动 `WebApplicationFactory` + Orleans silo + SQLite）只为断言一个纯函数。
- 真实 `git init/commit` 循环（runner `executor-*.spec.ts`）每个测试 ~秒级。共享 fixture、减少 commit 次数，或换 fake git。

参考预算：

| 轨道 | 单文件目标 | 单测试目标 |
|------|-----------|-----------|
| Unit | < 300 LOC, < 50ms/测试 | < 50ms |
| Spec | < 800 LOC（架构规则上限 24KB），< 500ms/测试 | < 500ms |
| E2E（Playwright） | 单独 `npm run test:e2e`，不进默认 `npm test` | < 5s |

并行与端口预算见下节。

### 5. 并行与端口预算

SpecTests 默认开 `parallelizeTestCollections`（xUnit 默认），按 collection 并行、collection 内串行。**最大并行度 = collection 数**，所以拆得越多越快——但每个并行 collection 都要起一个 silo，受端口预算约束。

- **超时预算（硬约束）**：单个测试 ≤ 5s，单个 collection（一组串行类）≤ 2min。CI 用进程级超时兜底；xUnit 无原生 per-test timeout，靠拆 collection 让慢启动分摊到并行。
- **禁止硬编码 clustering 端口**：`UseLocalhostClustering()` 默认 silo=11111 / gateway=30000。两个并行 silo 抢同一对端口 → host 构建卡死到 5min 超时（`Timed out waiting for the entry point to build the IHost`）。所有会并行的 silo fixture 必须用 `TestClusterPortAllocator` 分配随机端口，通过 `Mohist:Silo:SiloPort` / `Mohist:Silo:GatewayPort` 配置注入（`ConfigureMohistSilo` 读这两个 key，默认仍是 11111/30000，生产零影响）。
- **官方依据**：`dotnet/orleans` `test/Orleans.Runtime.Tests/LocalhostSiloTests.cs` 用 `TestClusterPortAllocator.AllocateConsecutivePortPairs` + `UseLocalhostClustering(siloPort, gatewayPort)`。Orleans 自己的测试项目反而全局串行（`parallelizeTestCollections: false`）——因为 `TestCluster`/`InMemoryTransport` 是 `internal`，外部不可达。我们靠"随机端口 + 配置注入"绕开，能在 web 测试里拿到并行。
- **collection 分配**：需要真实 silo 的 HTTP spec 按域分到 `IntegrationIssue` / `IntegrationApi` / `IntegrationSessions` / `IntegrationWorkflow` / `IntegrationRunner` / `IntegrationMisc` 等并行 collection（各 `ICollectionFixture<MohistIntegrationFixture>`，无 `DisableParallelization`）。操作进程全局资源的类（`RunnerRegistryKeys.Global`、`IManagementGrain.ForceActivationCollection`、跨类 `FakeTimeProvider.Advance`）留在串行 `MohistIntegration`（`DisableParallelization = true`）。
- **同一 DI root**：`MohistIntegrationFixture` 通过 `WebApplicationFactory<Program>` 让 web 和 silo 共享一个 `IServiceCollection`，`InMemoryEventBus` 等单例天然单实例——跨 grain/web 的事件流动无需桥接。拆 collection 不破坏这一点，因为每个 collection 是独立进程内 host、独立 bus，互不干扰。

### 6. 简洁、无冗余、读得出 spec

- 测试体读起来就是产品规约：`Arrange minimal → Act → Assert observable outcome`。冗长的 setup 抽到 helper / fixture。
- 同一产品能力不在两个文件里各写一遍。迁移式拆分完成后必须删旧文件，不允许新旧并存。
- 公共 fixture / fake / setup 放 `Support/`（C#）或 `tests/support/`（TS），不要在每个 spec 里 `cp` 一份。
- "一次性回归"（`*RegressionSpecs` / `issue-N-regression`）只有在没有等价常规测试时保留；一旦等价覆盖存在，删掉回归文件，把"issue #N"写进常规测试的注释。
- "一次性迁移"（`*MigrationSpecs`）带"release X 后删除"注释，到期删。

### 7. 一个文件只测一个被测对象

轨道与放置见上节；这里只约束粒度。文件名、目录、`describe` 标题三者要一致表达"验证的是什么"，且一个文件只测一个被测对象。

`SessionPage.test.tsx` 同时测 `SessionTranscriptView`（widget）+ `SessionPage`（page）+ `ToolRegistry`（model）是反例——拆成三个文件，各归各的轨道。

## 各端 fake 入口速查

### server（C#）

- 时间：`Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider`，注册进 `GrainTestConfig` / `MohistIntegrationFixture`（`MohistIntegrationFixture` 已注入 fake time，默认起点 `2026-06-30`）。
- HTTP：`WebApplicationFactory<Program>` + `TestServer`，客户端走 `HttpClient`，不经真网络。
- Grain：`InProcessTestCluster`（`WorkflowGrainFixture` 等），`ControllableReminderTable` 做确定性 reminder 控制。
- DB：in-memory shared-cache SQLite + keeper 连接。`MohistDbFixture` 用于 DI + EF，`MohistIntegrationFixture` 用于全栈。
- 第三方端口：`FakeGitService`、`FakeRunnerWorkspaceClient`、`RecordingRunnerHubContext`、`RecordingIEventPublisher`、`InMemoryStateStore`、`NoopEventStore`。
- 测试数据：`Support/TestData/*` 工厂，**禁止在 fixture 里用 `DateTime.UtcNow` / `Guid.NewGuid` 造可被断言的数据**。

### runner（TS）

- 时间：`vi.useFakeTimers()` + `vi.setSystemTime()` + `vi.advanceTimersByTimeAsync()`。`merge-github-pr.spec.ts:553-660` 是 PR 轮询 fake 时间的范式。
- 进程：`setAcpProcessFactoryForTest(...)`（ACP）/ `vi.spyOn(system.process, ...)`（git/shell）。
- 网络：`vi.stubGlobal('fetch', vi.fn())` 或 `vi.mock("../src/server/connection.js", ...)`。
- SignalR：`vi.mock("@microsoft/signalr", ...)`。
- workspace：`tests/support/workspace-mock.ts`（`verifyOnlyWorkspaceManager`）。
- ACP：`tests/support/fake-acp.ts` / `tests/acp/support.ts`（`FakeAcpAgent`、`FakeSharedAcpAgent`、`FakeServerConnection`）。

### web（TSX）

- 渲染：`tests/test-utils.tsx` 的 `customRender`（已包 `QueryClientProvider` + `ProjectProvider` + `Router`）。
- 数据：`createQueryClient()`（`retry: false`，**应为 `gcTime: 0`**），优先 MSW `setupServer`，或 `vi.mock('entities/.../api/client')`。
- 网络：MSW（推荐，spec 级）或 `vi.stubGlobal('fetch')`（unit 级），二者不要混用。
- SignalR：`vi.mock("@microsoft/signalr", ...)`。
- 时间：默认 `vi.useFakeTimers()` 不全局开；时间相关组件的测试自己开 + `afterEach` 关。
- e2e / a11y：`tests/e2e/`、`tests/a11y/`，由 `npm run test:e2e` / `npm run test:a11y` 触发，**不进默认 `npm test`**。所有 `page.route('**/api/**', ...)` 全量 mock。

### cli（C#）

- HTTP：`Support/RecordingHttpHandler.cs`（捕获 + 可注入 responder）。**禁止裸 `new HttpClient()`**。
- 进程：`Support/FakeCommandExecutor.cs`（no-op）或 `ScriptedCommandExecutor`（脚本化输出）。
- 文件：`Support/FakeFileSystem.cs`（in-memory）。
- 环境：`MockEnvironmentVariableProvider`。
- 时间：`ServiceReadinessProbe` 等时间逻辑需补 `TimeProvider` seam（当前缺失）。
- OTel 查询：用 fake `IOtelQueryExecutor`，**不要开真 SQLite 文件**（当前 `CliOtelCommandSpecs` 违规）。

## 审查清单

提 PR 前自查，审查时对照：

1. 这条测试是 spec 还是 unit？文件名/目录/describe 是否一致表达？
2. 它触碰真实网络/进程/git/DB/系统服务了吗？有任何 `localhost:*` 真绑定吗？
3. 它用了真实墙钟吗？（`DateTime.UtcNow` / `Date.now()` / `while(now<deadline)` / `Task.Delay(settle)` / `elapsed < N` 断言）
4. 它会 flaky 吗？有任何顺序依赖 / 时间戳种子 / 未恢复的 stub 吗？
5. 它有重复覆盖吗？有没有另一个文件测同一件事？是不是迁移残留？
6. 它的 setup 能不能抽到共享 helper？fixture 启动成本是不是过度？
7. 读测试体能看出产品规约吗？还是淹没在 mock 装配里？
