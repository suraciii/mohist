# 收敛 .NET 测试为 SpecTests、UnitTests 和 ArchTests

> **执行者规则**：测试分类只看它证明什么，不看它用了 HTTP、SQLite、Orleans、
> DI、完整宿主还是纯函数。先完成测试来源清单，再移动文件或项目。不能因为一个
> 测试慢就发明新的测试种类，也不能因为一个测试用了数据库就自动叫 Component。
> 遇到 STOP 条件立即报告，不要自行创造第四条轨道。
>
> **Drift check first**：
>
> ```bash
> git diff --stat b1462579e..HEAD -- \
>   Mohist.sln \
>   design/testing.md \
>   packages/server/tests \
>   packages/cli/tests \
>   packages/server/src/Mohist.Server/AssemblyInfo.cs \
>   .github/workflows/ci.yml
> ```
>
> 如果测试项目、类名或下文统计已变化，先更新本计划，不要把旧分类强套到新代码。

## Status

- **Priority**: P1
- **Effort**: L，先审计，再按项目逐步迁移
- **Risk**: MED，主要风险是把产品规格误当实现细节，或把实现测试误当产品承诺
- **Depends on**: `plans/dotnet-test-suite-simplification.md` 的已完成基础工作
- **Category**: tests / architecture / performance
- **Planned at**: commit `b1462579e`, 2026-07-11

## Final decision

.NET 只有三种测试：

| Test kind | 权威来源 | 验证对象 | 失败代表什么 |
|---|---|---|---|
| `SpecTests` | 产品承诺；可以记录在 `docs/`，也可以由代码中的可执行产品契约表达 | 产品行为与用户流程 | 产品行为回归 |
| `UnitTests` | 技术设计、类型/模块接口和实现不变量 | 技术实现 | 实现契约回归 |
| `ArchTests` | `design/` 中已经决定且稳定的架构规则 | 架构一致性、依赖、边界、放置、命名和禁止事项 | 实现不再符合架构决定 |

`Component`、`Integration`、`E2E`、`a11y`、`migration`、`database`、`grain`、
`full-host` 都不是测试种类。它们只能描述测试使用的技术、范围、执行 profile 或
测试主题。

`TestSupport` 是普通 class library，不引用 test SDK，不发现测试，因此不算第四种
测试。

## Testing principles

### 分类看权威来源，不看运行方式

先问“这个 expected value 属于谁的承诺”：

- 是用户、operator 或 API/CLI consumer 可观察且产品有意承诺的行为，放 `SpecTests`；
- 只能从内部 class/function、store、querier、grain、serializer 或技术设计推出，放
  `UnitTests`；
- 是对目录、依赖、程序集、命名、项目引用或禁用 API 的稳定限制，放
  `ArchTests`；
- 找不到权威来源，不要仅为保住 coverage 而保留含糊测试。先判断它是否保护当前仍然
  存在的独有风险：是产品承诺则确认其契约证据，是稳定架构决定则补设计依据，是实现
  契约则归 Unit；没有当前契约或独有风险就删除。

一个 Spec 可以使用纯函数，也可以启动完整 host。一个 Unit 可以使用真实的内存
SQLite、DI 或 `InProcessTestCluster`。这些选择只影响 setup 和速度，不改变测试
种类。

### 用重构问题做最后判断

- 完全重写内部实现但保持产品行为时，Spec 应继续通过；
- 重构某个技术模块时，对应 Unit 可以合理变化；
- 只有明确修改架构决策时，对应 Arch 规则才可以放宽或删除；使用 ratchet 时，阈值
  收紧属于偿还已知架构债务。

如果一个测试无法回答这三个问题中的哪一个适用，它的边界还没有说清楚。

### 产品规格可以在文档，也可以在代码

`docs/` 不是产品规格的完整清单，缺少文档不能自动把一个测试降为 Unit 或判为无价值。
现有产品契约可以由以下证据表达：

- `docs/` 中的产品场景、命令和行为说明；
- HTTP route、request/response contract、CLI command/help/output、公开错误语义；
- 对用户可见的领域状态转换，以及 Web/CLI/Runner 等真实 consumer 所依赖的行为；
- 已经用产品语言描述、通过产品边界驱动并被确认有意表达当前行为的 executable spec。

代码证据仍须说明“产品承诺是什么”。一个 `public` C# symbol、private branch、数据库
shape 或测试文件的 `Specs` 后缀本身都不足以证明产品规格。被测 method body 如果本身就是
产品边界，并且该可观察行为是有意契约，可以作为证据；偶然的算法和分支不能。最简单的
判断仍然是：内部实现完全重写而用户可观察行为不变时，这项测试是否应该继续通过。

审计已有测试时，code-only product spec 可以直接保留并在 ledger 记录具体 path/symbol、
consumer 或 executable behavior；不要求额外补文档。只有当行为是否属于
产品承诺本身不明确时才 `investigate`。新增产品行为仍遵守仓库的 spec-first 原则，不能
借“代码也是 spec”绕过产品决策。

### SpecTests 只说产品语言

- 一个文件对应一个产品能力或规格章节，不对应一个内部 class；
- 测试名描述用户动作、可见状态、错误和输出，不描述 handler/store/row；
- 优先通过 HTTP、CLI command 或产品入口驱动；
- 允许直接 seed DB 或使用 fake 创建难以通过产品入口构造的前置状态；
- setup 可以知道实现，最终 assertion 必须是产品可观察结果；
- 不直接断言 DI registration、数据库 row、内部 event store 或 private serialization；
- 使用 HTTP、CLI 或完整 host 不会自动让测试成为 Spec；每个 expected value 和分支仍须
  能说明具体产品承诺，并给出文档或代码契约证据；
- API matrix 的每个 case 必须代表产品明确区分的场景。仅为覆盖计算分支、null/zero
  组合或内部实现路径的 rows 应折叠、移到 Unit，或删除；
- code-only spec 可以保留；但不能仅从同一个被测实现的分支机械反推出产品承诺。

### UnitTests 只说技术实现

- 一个文件对应一个 cohesive implementation subject；
- 可以验证纯函数、parser、mapper、service、store、query、grain、adapter、migration、
  persistence 和技术错误语义；
- 使用 subject 所需的最低自然 seam；不要求所有 collaborator 都 mock；
- 技术测试可以使用内存 DB、DI 或 in-process runtime，但不得触碰真实外部环境；
- 不重复已经由 SpecTests 完整表达的产品场景，除非它保护一个独立技术不变量；
- 不因为实现里存在一个 class、branch 或 mapper 就必须有对应 Unit；只保留能捕获独立、
  当前且可行动的技术回归的测试；
- 文件和 class 使用 `*Tests`，即使它内部使用 SQLite 或 Orleans。

### ArchTests 验证架构约束

- 每条规则必须能指向 `design/architecture.md`、`design/domain-analysis.md`、
  `design/conventions.md` 或 `design/testing.md` 中的稳定决定；
- ArchTests 是成熟的架构测试类别，不是 ratchet 的同义词。它可以验证依赖方向、分层、
  模块边界、目录放置、项目引用、命名、禁用 API/模式和可静态检查的架构质量；
- 它们是可执行的 architecture fitness functions，可以检查 assembly、source tree、
  project graph 或其他确定性的架构模型；
- 本仓库的 ArchTests 采用确定性的结构检查，不启动 host、DB、网络或 Orleans，也不验证
  产品行为；这是一项仓库内实现选择，不是对 ArchTests 概念的重新定义；
- 没有既有违规时直接断言目标不变量；不要为了使用 ratchet 而使用 ratchet；
- ratchet 只是处理既有架构债务的一种可选执行策略：当前还不能满足最终不变量时，允许
  用有依据、可单调收紧且有退出条件的基线阻止继续恶化；
- `[Fact(Skip = ...)]` 不提供任何主动保护。尚未满足的规则必须选择：修复后直接启用、
  建立有明确债务来源和退出条件的 ratchet，或移回 design gap / issue；
- 文件大小、耗时排名、违规计数和 allowlist 只有在能可靠代表某项架构质量时才可作为
  ratchet 信号，不能把偶然的当前形状包装成架构真理；
- 放宽或删除架构约束必须先修改设计决定；ratchet 收紧时同步更新债务记录。

### 有测试不等于有价值

分类之前先判断测试是否值得存在。每个 test method，或保护同一契约的一组 theory rows，
必须回答：

1. 它能独自捕获什么具体回归？
2. expected behavior 属于当前产品承诺、技术契约还是架构约束？证据记录在哪里？
3. 是否已有另一个测试在相同或更合适的 seam 捕获同一风险？
4. assertion 是否观察 subject 的结果，而不是重复 setup、mock、fixture 或 framework 行为？
5. 被测行为是否仍可到达、仍受支持、仍与当前产品和设计有关？
6. 它的维护成本和运行成本是否与信号相称？

审计结论只有以下几种：

- `keep`：保护独有、当前、有权威来源且可行动的回归；
- `rewrite/reclassify`：风险有价值，但语言、driver、断言或测试种类错误；
- `collapse`：多项测试重复同一规则，只保留最自然的 owner 和必要代表场景；
- `delete`：重复、过时、不可达、同义反复、只测 mock/setup/framework、未驱动命名
  subject，或没有当前契约与独有风险；
- `investigate`：看起来可能形成新的产品承诺或架构决定，但现有权威来源不足；停止该项，
  不从测试实现自行发明规则。

历史上修过 bug、增加 coverage 或运行很快，都不能单独证明测试有价值。删除无价值测试
不要求虚构一个 retained owner；必须记录“没有当前契约/风险”等可复核理由。

### 速度按机制管理，不按种类分项目

目标预算：

| Runtime mechanism | Target |
|---|---|
| pure/in-memory code | 单 test < 50ms |
| SQLite、DI、in-process grain | 单 test < 500ms |
| full host / product flow | 单 test < 5s，共享 collection < 2min |

慢测试先缩小 setup、共享只读模板、删除重复场景或选择更稳定的产品 driver。不能通过
新增 `ComponentSpecs`、`IntegrationSpecs`、速度 trait、数字 shard 或自定义 orderer
解决。

### 三种测试共享的硬规则

- 不访问真实网络、进程、git、DB 文件、系统服务或用户环境；
- 不使用真实时间，注入 `TimeProvider` / fake timers；
- 任意顺序、任意并行度、重复运行结果一致；
- 不新增 skip 掩盖 flaky；
- setup 小而局部，共享 helper 只隐藏真实重复知识；
- 一个行为只有一个权威 owner，重复 coverage 必须有不同风险理由；
- collection 只表达共享 lifetime 或真实隔离边界，不表达速度和分类。

## Target project model

Server 最终只有：

| Project | Role |
|---|---|
| `Mohist.Server.SpecTests` | Server 产品规格 |
| `Mohist.Server.UnitTests` | Server 技术实现 |
| `Mohist.Server.ArchTests` | Server 及相关 .NET 边界的架构约束 |
| `Mohist.Server.TestSupport` | 非 test library，仅存放两个 test project 真正共享的 deterministic support |

CLI 最终只有：

| Project | Role |
|---|---|
| `Mohist.Cli.SpecTests` | `mo` 命令面、输出、错误和用户流程 |
| `Mohist.Cli.UnitTests` | parser、renderer、updater、probe、API client 等技术实现 |

如果 CLI 的 Spec 和 Unit 确实共享同一批 fake，可以创建不含 test SDK 的
`Mohist.Cli.TestSupport`。先尝试把 helper 放在唯一 owner project；不要为了 DRY
提前创建跨项目 junk drawer。

不创建 `Mohist.Cli.ArchTests`，除非出现独立且稳定的 CLI 设计规则族。当前跨项目
test-project 和源码边界继续由现有 `Mohist.Server.ArchTests` 保护。

## Current state

当前 solution 的测试形状：

| Project | Files | `[Fact]` / `[Theory]` | Current problem |
|---|---:|---:|---|
| `Mohist.Server.UnitTests` | 113 | 1240 | 定义过窄，只允许 pure code |
| `Mohist.Server.ComponentSpecs` | 195 | 1516 | 按依赖机制分类，混合产品规格与技术实现 |
| `Mohist.Server.IntegrationSpecs` | 102 | 958 | 按 host 机制分类，混合 HTTP spec 与 direct-service/grain tests |
| `Mohist.Server.ArchTests` | 2 | 35 | 项目种类正确；每条规则仍需价值/依据审计，且有 3 个 skipped aspirations |
| `Mohist.Cli.Tests` | 54 | 834 | 项目名无类型；36 个 `*Specs` 与 9 个 `*Tests` 仍有语义误名 |

`design/testing.md` 同时声明了 Spec/Unit、第三类 ArchTests，又定义
ComponentSpecs/IntegrationSpecs，原则互相冲突。当前新计划也错误地把“最低运行层”
当成测试类型；本计划取代该模型。

已完成且应保留的基础工作：

- custom orderer、cost map、Speed/SUT traits 和数字 collections 已删除；
- migrated SQLite template 与 schema 单一来源已建立；
- TestSupport 已是普通 library；
- repo-root ArchTests 和项目边界检查已可用；
- PR 118 的 .NET/Node CI 当前绿色。

## Authenticity and value checklist

审计必须读到 test method 和 assertion。只有多个 methods/theory rows 保护同一来源、同一
风险且采取同一动作时，ledger 才能合并成一行；仅按 file/class 填一行不够。

| Field | Required content |
|---|---|
| Current method/group | 精确到 test method，或同一契约的方法组 |
| Authority / evidence | Spec 写明产品承诺和 `docs/` 或 code path/symbol/consumer；Unit 写 implementation contract；Arch 写 design decision |
| Unique regression | 该测试独自捕获的具体、当前风险 |
| Value verdict | keep、rewrite/reclassify、collapse、delete 或 investigate |
| Test kind | Spec / Unit / Arch 三选一 |
| Observable assertion | 用户结果、技术结果或结构规则 |
| Runtime mechanism | pure、DB、grain、host、CLI 等，仅作执行信息 |
| Action | keep、move、rename、split 或 delete |
| Retained owner/reason | 重复项给保留 owner；无价值项给删除理由；否则 `none` |

不能仅凭当前项目、namespace、文件后缀或是否使用 `HttpClient` 自动分类。所有当前
`*Specs` 都要重新证明自己在验证产品规格；名字只提供审计线索，不提供结论。

## Initial audit leads

这些是开始审计的线索，不代替 method-level ledger：

| Current test | Initial verdict | Reason |
|---|---|---|
| `AgentCostRollupApiSpecs`、`AgentUsageTimeseriesApiSpecs` | investigate as Spec | `docs/` 没有覆盖不代表不是 Spec；检查 endpoint contract、DTO、真实 consumer 和 executable behavior，逐项确认产品区别 |
| `AgentUsageReporterSpecs` | reclassify to Unit candidate | 直接验证 reporter 方法与计算实现；仍须确认是否保护独立技术不变量 |
| `AgentActivitySpecs` | retain as code-only Spec | AgentOps activity feed 是产品读模型；检查 card/summary 是否与 route spec 保护不同风险 |
| `AgentSubscriptionLaunchVisibilitySpecs` / `AgentLauncherTests` | split Spec and Unit | 订阅来源可追溯是产品承诺；参数 guard 是直接技术契约 |
| `RunnerWorkflowTerminalStatusHandlerSpecs` | delete candidate | 没有驱动 handler，且 router Unit coverage 已存在 |
| `IssueWorkflowLifecycleSpecs` | investigate as Spec | complete/cancel/start/rerun 看似产品行为；当前 direct grain driver 不决定类型，先找到具体产品规格并检查可观察结果 |
| `IssueRepositoryReferenceSpecs` | delete candidate | 创建、配置变更和 repository problem 已分别由 repository API、resolution regression 和 resolver Unit 覆盖；内部 JSON shape 不单独构成产品规格 |
| `IntegrationFixtureSchemaSpecs` | value investigation | 只验证测试基础设施和 migration-shaped schema；若真实 migration/consumer tests 已覆盖该风险，应删除而不是搬家 |
| `ArchitectureRules` | rule-by-rule Arch audit | 逐条确认架构依据；ratchet 仅用于有意保留的既有债务 |
| `CliIssueCommandSpecs`、`CliWorkflowReadsSpecs` | Spec candidates | 用户命令、输出和错误看似产品契约；逐方法匹配文档或 command/help/output 等代码契约 |
| `SystemdUnitParserTests`、`InfoRendererTests` | Unit candidates | parser/renderer implementation；仍须证明独立技术风险 |
| `RuntimeConsistencyValidatorSpecs`、`ServiceReadinessProbeSpecs` | reclassify to Unit candidates | 直接验证技术 service，应审计价值并将保留项改名 `*Tests` |

当前抽样已经观察到：

- `RunnerWorkflowTerminalStatusHandlerSpecs` 的 3 个 tests 都直接 resolve 并调用
  `IRunnerWorkflowStatusRouter`，没有驱动 class 名称中的 handler；connected、offline 和
  no-assignment 三个结果又分别由 `RunnerWorkflowStatusRouterSpecs` 覆盖。这是高置信度删除
  候选，不值得连同 full-host setup 一起迁移；
- `AgentUsageReporterSpecs` 有 20 个 direct-reporter tests，两个 metrics API files 又有 50 个
  API tests，其中大量窗口、bucket、empty/zero 和 cumulative 语义重叠。执行时应先确认
  产品真正承诺哪些区别，再让 API Spec 与 reporter Unit 各自只拥有独立风险；
- 当前 `docs/` 没有 cost/usage 说明不能推出任何分类结论。必须继续读 endpoint contract、
  response types、Web/CLI consumer 和现有 executable specs，判断哪些数值语义确实是产品
  行为；code-resident contract 本身可以成为充分依据；
- `AgentCostRollupApiSpecs` 中“remains available/unchanged”和“additive preservation”类测试
  看起来保护历史实现关系，不自动构成当前产品规格；在本项目不要求版本兼容的前提下，
  若找不到当前产品契约或独立风险，应删除；
- `IntegrationFixtureSchemaSpecs` 只读取 fixture database 的 table/index/column shape。它必须
  与 migration/template/consumer coverage 对账，不能因为保护 test fixture 就默认保留；
- 现有 3 个 skipped cycle rules 没有主动保护作用，但其架构意图可能有价值，必须分别在
  直接 invariant、ratchet 和 design gap/issue 之间做决定。

## Scope

**In scope**：

- `design/testing.md`；
- `Mohist.sln`；
- `.github/workflows/ci.yml` 和 root test commands，仅在项目名变化时更新；
- `packages/server/tests/`；
- `packages/cli/tests/`；
- `packages/server/src/Mohist.Server/AssemblyInfo.cs` 的 `InternalsVisibleTo`；
- 本计划的迁移、删除和验证记录。

**Out of scope**：

- 产品行为和公开 API 改动；
- migration squash 或 production schema 改动；
- Web/Runner test file 的全面重命名；它们遵守同一三类原则，但由 Node test plan 执行；
- 新 test framework、global host pool、custom orderer、cost map、速度 trait、数字 shard；
- 为移动测试而重构产品代码；
- archived OpenSpec history。

**Read-only evidence**：允许读取 `packages/server/src/`、`packages/cli/`、
`packages/web/src/`、`packages/runner/src/`、`docs/`、`design/`、`openspec/` 和相关 git
history 来确认 code-only product contracts；这些路径不因审计而自动进入修改范围。

## Baseline commands

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true
dotnet test Mohist.sln -p:SkipWebBuild=true --no-build

dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.ComponentSpecs/Mohist.Server.ComponentSpecs.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.IntegrationSpecs/Mohist.Server.IntegrationSpecs.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj \
  -p:SkipWebBuild=true --no-build
```

将 `--list-tests` 输出写入 `/tmp`，保存 normalized class/method identity 和
total/pass/skip。项目与 namespace 重命名需要显式 mapping；不能用总数相近代替逐项
核对。

## Work chunk: 先改测试原则

先修改 `design/testing.md`：

- Tracks 只列 Spec、Unit、Arch；
- 删除“ArchTests 是第三种例外”的矛盾写法，直接把它定义为正式轨道；
- 删除 ComponentSpecs/IntegrationSpecs 作为 test kind 的定义；
- 增加“runtime mechanism 不是 test kind”；
- 按本计划重写 Spec、Unit、Arch 原则；
- 速度预算改按 runtime mechanism，不按 track；
- E2E/a11y 明确是 Spec execution profile；
- TestSupport 明确不是 test；
- parallelism/collection 规则适用于所有 .NET tests；
- ArchTests 定义为完整的架构约束测试类别，ratchet 只是处理既有债务的可选策略；
- 增加 Spec 真实性和测试价值规则：产品契约可以在文档或代码，缺文档不自动降级；
- 明确无价值测试应删除；
- active ArchTests 必须通过，不能用 skip 表达未来目标。

gate：设计文档能在不提当前项目历史的情况下独立回答任意测试该放哪里。

## Work chunk: 完成全量真实性与价值审计

审查 Server 四个 test projects 和 `Mohist.Cli.Tests` 中的每个 test method。先读测试名、
assertion、driver 和 subject，再填写 Authority/evidence、Unique regression 与 Value verdict；
不要按目录、class 或后缀批量判定。相同 contract 的 methods/theory rows 可以在 ledger
中合并，但必须共享同一来源、风险和动作。

对所有当前 `*Specs` 做真实性检查：

- 用一句产品语言写出它保护的承诺，并记录精确的文档或代码契约证据；
- code evidence 可以是 route/command/DTO/domain transition/consumer/executable spec 的具体
  path 和 symbol；若证据就是被测 product boundary，必须写出它承载的可观察契约，不能
  只引用内部算法或 branch；
- 测试名和 assertion 必须能用产品语言复述；
- HTTP/CLI/full host 只说明 driver，不证明产品规格；
- 同一 matrix 中没有独立产品意义的 rows 不作为独立 Spec 保留；
- mixed file 按 method 拆分，不能用 file-level majority 决定整类归属。

Spec candidate 找不到文档依据时：

- 继续检查 product boundary、contract types、真实 consumer、领域状态和 executable spec；
- 若产品承诺清楚，保留为 Spec 并记录 code evidence；不强制补文档；
- 若只是实现行为，改为 Unit；
- 若行为是否属于产品承诺仍不明确，标为 `investigate` 并报告，不把偶然 method body
  behavior 自行升级成产品承诺；
- 若已有相同产品场景 owner，折叠或删除重复；
- 若既无产品承诺也无独立技术风险，直接删除并记录理由。

对所有测试同时执行价值检查：重复、过时、不可达、同义反复、只测 setup/mock/framework、
没有驱动命名 subject 或没有当前契约的测试，标为 delete；有价值但 seam/语言/类型错误的
标为 rewrite/reclassify。不能先假设全部现有测试都要迁移。

Arch rule 找不到 `design/` 依据时不保留为 ArchTest；当前不能满足最终不变量时，必须在
直接修复、带退出条件的 ratchet、或 design gap/issue 三者中明确选择。

gate：所有 test methods 都被 ledger 单独覆盖或属于明确的方法组；每项都有
authority/evidence、
unique risk、value verdict、kind 和 action，且没有 `unknown`。`investigate` 项必须先解决，
不得进入迁移。

## Work chunk: 先清理无价值和错误 owner

在原项目内先执行 ledger 中的 `delete`、`collapse` 和必要的 `rewrite`：

- 删除没有驱动其命名 subject 的测试，例如当前
  `RunnerWorkflowTerminalStatusHandlerSpecs`；
- 对 API matrix 只保留产品规格明确区分的场景，把独立技术不变量交给自然的 Unit owner；
- 若 `IntegrationFixtureSchemaSpecs` 只重复真实 migration/consumer coverage，删除；否则把
  最小且独有的基础设施风险改成 Unit；
- 删除旧行为、不可达状态、fixture self-test、mock interaction mirror 和无意义 wrapper；
- mixed Specs 先按产品 owner 与技术 owner 拆开，再移动项目；
- 每个删除记录 retained owner，或记录没有当前契约/独有风险的理由。

每个小批次运行原项目。intentional test-count reduction 必须与 ledger 对得上。

gate：没有待处理的 delete/collapse/investigate；每个保留测试能用一句话说明独有风险，
所有剩余 `*Specs` methods 都能说明产品承诺，并有文档或代码契约证据。

## Work chunk: 建立 Server 三项目模型

按 authenticity/value ledger 迁移：

1. 将 `Mohist.Server.IntegrationSpecs` 重命名为 `Mohist.Server.SpecTests`，但只保留
   ledger 判定为产品规格的文件；
2. 将 Component/Integration 中的技术实现 tests 移入 `Mohist.Server.UnitTests`，
   文件、class、namespace 从 `*Specs` 改成 `*Tests`；
3. 将 Unit/Component 中确认属于产品规格的文件移入 SpecTests，并改成产品语言；
4. 删除空的 `Mohist.Server.ComponentSpecs` 和旧 IntegrationSpecs project；
5. 保留 `Mohist.Server.ArchTests`，只迁移真正的架构约束；
6. TestSupport 只保留 SpecTests 与 UnitTests 共同需要的 deterministic support；
7. 更新 solution、project references、assets、collections、assembly visibility 和 CI path。

不能通过 linked source file 或 test-project reference 共享测试。小 helper 允许各项目
各自拥有；共享知识真实存在时才进入 TestSupport。

每个 move 后分别运行 source project 与 target project；任何 discovery 缺失立即停止。

## Work chunk: 拆分 CLI Spec 与 Unit

创建：

- `Mohist.Cli.SpecTests`；
- `Mohist.Cli.UnitTests`。

命令 help、参数、HTTP request、表格/JSON 输出、exit code 和错误文案属于 Spec。
parser、renderer、updater、probe、synchronizer、validator 和 API envelope internals 属于
Unit。不能只按当前 `Specs`/`Tests` 后缀批量移动；例如
`RuntimeConsistencyValidatorSpecs` 应审计为 Unit 并改名。

删除旧 `Mohist.Cli.Tests` project。两个项目都需要的 fake 先尝试缩小或复制；只有
共享知识明显且稳定时创建非 test 的 `Mohist.Cli.TestSupport`。

gate：CLI SpecTests 与 UnitTests 独立绿色，旧 project/namespace 搜索为空。

## Work chunk: 迁移后收口语言和 driver

项目迁移后只做边界收口，不把价值审计拖到此处：

- Spec 使用产品语言和产品结果；内部 DB/service 只允许 setup/probe；
- Unit 使用直接 subject，不为模拟产品入口启动不必要的 full host；
- 删除旧类型名、无意义 wrapper fixture 和只为跨项目搬运存在的 helper；
- 搜索 implementation subject 名称残留在 Spec class/test names 中；
- 搜索产品语言残留在只验证技术结果的 Unit names 中。

gate：项目名、namespace、文件名、test name、driver 和 assertion 表达同一种 owner。

## Work chunk: 整理 ArchTests

审查 `ArchitectureRules.cs`：

- 每条规则独立记录对应 design path/section、架构质量和失败后的行动；
- 逐条处理 3 个 skipped internal-cycle rules：若当前可修复则直接启用最终不变量；若是
  有意保留的迁移债务则建立有依据、可单调收紧且有退出条件的 ratchet；否则移回
  design gap/issue。不能继续 skip；
- 更新 project-name、namespace、TestSupport、no-test-project-reference guards；
- 删除基于 Component/Integration 的 package guards；
- 增加只允许 `*.SpecTests`、`*.UnitTests`、`*.ArchTests` 的项目类型 guard；
- 保留 no traits、no custom orderer、known process-global collection guards；
- ArchTests 自身不得依赖 runtime test host、DB 或网络；
- 删除不能代表稳定架构质量的偶然 shape guard；不要把测试数量、文件大小或当前目录清单
  当成默认架构规则。

从 repo root 和 ArchTests project directory 各运行一次，必须检查同一 repository
root 并得到相同结果。

## Final project commands

```bash
dotnet build Mohist.sln -p:SkipWebBuild=true

dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/cli/tests/Mohist.Cli.SpecTests/Mohist.Cli.SpecTests.csproj \
  -p:SkipWebBuild=true --no-build
dotnet test packages/cli/tests/Mohist.Cli.UnitTests/Mohist.Cli.UnitTests.csproj \
  -p:SkipWebBuild=true --no-build

dotnet test Mohist.sln -p:SkipWebBuild=true --no-build
git diff --check
```

结构搜索：

```bash
rg -n 'ComponentSpecs|IntegrationSpecs|Mohist\.Cli\.Tests' \
  Mohist.sln design/testing.md packages/server/tests packages/cli/tests \
  packages/server/src/Mohist.Server/AssemblyInfo.cs .github/workflows

rg -n 'ITestCollectionOrderer|TestCollectionOrderer|CostDescendingCollectionOrderer|NamedCollectionCost|Traits\.(Speed|Sut)|\[Trait\(' \
  packages/server/tests packages/cli/tests design scripts

rg -n '\[Fact\([^]]*Skip|\[Theory\([^]]*Skip|Skip\s*=' \
  packages/server/tests/Mohist.Server.ArchTests

rg -n 'ProjectReference.*(SpecTests|UnitTests|ArchTests)' \
  packages/server/tests packages/cli/tests -g '*.csproj'
```

全部搜索必须为空。最后一个搜索用于禁止 test project 互相引用；TestSupport 是普通
library，不匹配该规则。

## Final verification

1. build 一次；
2. 五个最终 test projects 各运行一次；
3. full solution 正常运行两次；
4. 固定四核 full solution 运行两次；
5. normalized discovery 对账；
6. total/pass/skip 对账，所有减少都有 mapping；
7. 所有结构搜索；
8. `git diff --check`；
9. 检查没有 Scope 外改动。

固定四核：

```bash
/usr/bin/time -f 'elapsed=%e' \
  taskset -c 0-3 dotnet test Mohist.sln \
  -p:SkipWebBuild=true --no-build
```

最终中位数不得比 `b1462579e` baseline 慢 10%。如果合并 test assemblies 暴露 fixture
竞争，修复 state ownership；不能恢复第四种项目、速度 shard 或自定义调度。

## Done criteria

- [x] `design/testing.md` 只定义 Spec、Unit、Arch 三种测试；
- [x] runtime mechanism、E2E/a11y 和 TestSupport 明确不是 test kind；
- [x] Server 只有 SpecTests、UnitTests、ArchTests 三个 test projects；
- [x] CLI 只有 SpecTests、UnitTests 两个 test projects；
- [x] ComponentSpecs、IntegrationSpecs 和 generic `Mohist.Cli.Tests` 已删除；
- [ ] 每个保留的 test method 或同契约方法组都有权威来源、独有风险和价值结论；
- [ ] 每个 Spec method 能说明产品承诺，记录精确的文档或代码契约证据，使用产品语言并
      断言产品结果；
- [ ] code-only product specs 未因缺文档被误降级、删除或强制补文档；
- [ ] API/CLI/full-host tests 未因 driver 被自动视为 Spec；无产品意义的矩阵已折叠；
- [ ] 每个 Unit method 有明确且有价值的技术契约，不冒充产品规格或镜像实现细节；
- [ ] 每个 Arch rule 有设计来源、明确架构质量、当前通过且没有 skip；
- [ ] ratchet 只用于有依据的既有架构债务，并有单调收紧策略和退出条件；
- [x] TestSupport projects 不含 test SDK、xUnit、fixture、collection、host 或 test class；
- [x] 没有 test-project reference、linked test source 或 generic shared fixture；
- [x] 五个 test project 不直接访问墙钟或 `TimeProvider.System`；
- [ ] 没有测试直接访问真实网络、进程、git、shell、agent binary、DB file、系统服务、环境变量或
      用户 HOME；隔离的 temp directory 只用于其本身是 subject 或 fake backing store 的测试；
- [x] 每个删除的 test 有 owner 或明确无价值理由；
- [ ] 没有重复、过时、不可达、fixture self-test、mock mirror 或未驱动命名 subject 的
      已知低价值测试；
- [x] normalized discovery 无意外丢失或重复；
- [x] no new skip；ArchTests skip 为零；
- [x] custom orderer、traits、数字 shard 仍为零；
- [x] 五个 test projects 与 full solution 全绿；
- [x] 固定四核中位数不比 baseline 慢 10%；
- [x] `git diff --check` 为空；
- [x] 没有产品行为变化。

## STOP conditions

停止并报告，不要 improvisation：

- 某个 test 看似保护独有风险，但无法确定产品、技术或架构权威来源；
- Spec candidate 的文档与代码证据都不足，无法判断它是在表达产品承诺还是镜像实现；
- Arch rule 没有稳定 design decision，或既有违规需要新的 ratchet 策略但缺少明确债务
  边界与退出条件；
- 项目合并需要 test-project reference、linked source、global host pool 或 mega fixture；
- 删除 test 找不到 owner，且它验证独有风险；
- 一个产品行为在迁移后只剩内部实现 assertion；
- normalized discovery 出现无法解释的丢失或重复；
- skip 增加；
- 同一验证连续失败两次；
- 固定四核中位数回退超过 10%；
- 有人建议新增 Component/Integration/E2E test project、速度 trait、数字 shard 或
  custom orderer；
- 任务要求顺手迁移 Web/Runner tests 或修改产品行为。

## Execution record

### Authenticity and value ledger

| Current method/group | Authority / evidence | Unique regression | Verdict / Kind | Runtime | Action | State |
|---|---|---|---|---|---|---|
| `AgentUsageReporterSpecs` 20 methods | `AgentUsageReporter` aggregation; API specs retain endpoint contract | bucket/window/cumulative aggregation independent of route wiring | keep / Unit | SQLite + fake time | moved to `AgentUsageReporterTests` | DONE |
| `AgentActivitySpecs` 4 methods | `design/domain-analysis.md` AgentOps read-side; `/agent/activity` route | activity cards, waiting work and summary counters remain correct | keep / Spec | DI + SQLite | renamed from assembler subject to product activity; deleted two non-observing/default-only cases | DONE |
| `AgentSubscriptionLaunchVisibilitySpecs` 2 methods | `docs/agent-subscriptions.md`: system starts Agent and records the response relationship | subscription launch is traceable and manual launch does not claim subscription origin | keep / Spec | full host + grain | renamed from launcher subject and retained only visibility contract | DONE |
| `AgentLauncherTests` 5 methods | `AgentLauncher.LaunchAsync` input contract | invalid caller input fails before collaborators are used | keep / Unit | direct | moved validation cases out of host specs | DONE |
| `RunnerWorkflowTerminalStatusHandlerSpecs` methods | no independent source | `RunnerWorkflowStatusRouterSpecs` owns connected/offline/no-assignment behavior | delete / none | full host | deleted | DONE |
| Agent cost and usage API methods | `/agent/cost` and `/agent/usage` routes; Web Insights query clients and charts consume both DTOs | users see cumulative, daily, windowed spend, per-issue cost and usage history | keep / Spec | HTTP host | retained independent visible metric/range cases; deleted four duplicate or derived cases | DONE |
| `IssueWorkflowLifecycleSpecs` 12 methods | product loop, issue profile API, start readiness, path contract, completion handler and workflow-control specs | ten full-host cases have a natural owner; two start cases are user-observable command behavior | delete + rewrite / Spec | full host | deleted the mixed file; rewrote the two start cases as API-driven `IssueCreationSpecs` | DONE |
| `IssueCreationSpecs` start lifecycle 2 methods | `/issues/{number}/start` command surface and issue detail read model | repeated start never creates duplicate active work; start after stop binds a new workflow | keep / Spec | HTTP host + grain setup | added the two product-level cases at the existing issue creation boundary | DONE |
| `IssueRepositoryReferenceSpecs` 6 methods | `IssueRepositoryApiSpecs`, repository workflow/workspace specs and `IssueRepositoryResolverTests` | creation resolution, live configuration changes and each repository problem already have a stronger owner; direct state/DB setup adds no public contract | delete / none | direct DB + querier | deleted the mixed persistence/read-model file | DONE |
| `IssueRepositoryResolutionRegressionSpecs` 19 methods | `IssueRepositoryApiSpecs`, `IssueWorkflowRepositoryResolutionSpecs`, `IssueWorkspaceRepositoryResolutionSpecs`, `IssueRepositoryResolverTests` and `IssueRebaseRecoveryTests` | list metadata is a distinct API result; rebase input is a direct technical contract; the other host cases repeat an owner or force unreachable state | delete + rewrite / Spec + Unit | HTTP host + direct DB/grain | deleted the mixed regression file; moved the list case to repository API specs and rebase input to a direct Unit test | DONE |
| `IssueRepositoryApiSpecs` list metadata method | `GET /api/projects/{projectId}/issues` response contract | the issue list reflects the current repository context after configuration changes | keep / Spec | HTTP host | added a single list-specific case | DONE |
| `IssueRebaseRecoveryTests` task input method | `IssueRoutes.BuildRebaseTaskWith` implementation contract | a queued rebase task receives the resolved repository context | keep / Unit | direct | added direct builder coverage instead of inspecting workflow state through a host | DONE |
| `IssueWorkflowRepositoryResolutionSpecs` 4 methods | workflow variables are the runner contract; `/issues/{number}/start` is the user command | current repository metadata reaches workflow variables; a removed repository blocks start without creating work | keep + delete/rewrite / Spec | grain + HTTP host | retained two variable-contract cases, rewrote the removed-repository command case in `IssueCreationSpecs`, deleted the unreachable ghost-reference case | DONE |
| `IssueCreationSpecs` repository start method | `/issues/{number}/start` response and issue read model | removing an issue's repository prevents a new workflow from starting | keep / Spec | HTTP host + grain setup | added a concise `409`/no-workflow case | DONE |
| workspace diff after repository configuration change | `WorkspaceRoutes` diff contract | the user sees the current base branch after repository configuration changes | keep / Spec | HTTP host + fake workspace | retained | DONE |
| workspace read operations after repository removal | `WorkspaceRoutes` repository guard | diff, file content, workspace status and cleanup report configuration error instead of using a stale repository | keep / Spec | HTTP host + fake workspace | retained as one shared precondition scenario | DONE |
| rebase after repository branch change or removal | `IssueRoutes.Rebase` response contract | rebase uses the current base branch and refuses an unconfigured repository | keep / Spec | HTTP host + fake runner | retained | DONE |
| archive after repository removal | `IssueRoutes.Lifecycle` archive guard | archive refuses an unconfigured repository | keep / Spec | HTTP host | retained | DONE |
| `IssueWorkspaceRepositoryResolutionSpecs` setup | no behavioral contract | empty test lifecycle and an unused legacy path argument only obscure the actual branch setup | simplify / Spec | HTTP host | removed both without changing scenarios | DONE |
| `IssueCreationSpecs` default profile, querier, duplicate hydrate and workflow-status shape methods | initial creation case and `IssueVariableBuilderTests` | two cases repeat the same creation/read result; duplicate hydrate has no public command; property absence is source shape rather than a user result | delete / none | grain + host | deleted 4 methods; retained the separate grain-to-event-store persistence regression | DONE |
| `IssueCreationSpecs` direct prerequisite methods | create/prerequisite HTTP specs and `IssueStartReadinessDomainTests` | multiple, empty, duplicate, invalid, cross-project, self and completed prerequisites are already covered at the API boundary or as domain readiness logic | delete + strengthen / Spec | grain + querier | deleted 10 grain-path duplicates; strengthened the API case to cover two prerequisites | DONE |
| `IssueCreationSpecs` risk methods | issue create/read API; `IssueDomainTests` | API must return the risk chosen for an issue; allowed values, null and serialization are domain contracts | delete + rewrite / Spec | HTTP host | replaced four direct cases with one create/read API case | DONE |
| `IntegrationFixtureSchemaSpecs` method | `MigratedSqliteTemplate.CopyTo` schema contract | template must use current migrations, including unique label/workflow indexes | keep / Unit | SQLite | moved and renamed `MigratedSqliteTemplateTests` | DONE |
| migration source-text methods | no runtime contract; executable migration tests cover schema | none | delete / none | source text | deleted 2 methods | DONE |
| migrated-service registration matrix | implementation migration history, not a product or stable architecture contract | none; feature tests exercise concrete services | delete / none | DI + grain | deleted 112 theory cases | DONE |
| `OtelServiceRegistrationSpecs` methods | direct OtelDb tests and OTLP route tests own behavior | none | delete / none | DI + file-like DB | deleted 3 methods | DONE |
| `ArchitectureRules` layer, persistence and environment rules | `design/architecture.md` Server implementation boundaries | domain/API/data dependency direction and environment abstraction remain enforced | keep / Arch | assembly + project graph | documented the existing boundaries as the rule source | DONE |
| `ArchitectureRules` track, TestSupport, traits, ordering and collection rules | `design/testing.md` ArchTests, naming/placement and isolation rules | prevents a generic/fourth test project, test-project references, test support fixtures, execution shims and accidental parallel disablement | keep + simplify / Arch | source + project graph | naming rule now validates allowed track suffixes rather than a fixed project inventory | DONE |
| `DataStores_AreInInfrastructureData`, feature-directory, grain-name/location, EF entity-name, domain-module and Spec parser rules | no stable design decision; several assert current shape, framework discovery, or a bidirectional exception table rather than an architecture invariant | none | delete / none | assembly + source scan | removed nine low-signal Arch rules; the domain model remains a design gap for a future direct, directional constraint | DONE |
| domain internal-cycle rules | no stable design decision; exceptions and skips leave no current invariant | none | delete / none | assembly scan | deleted one exception-based rule and three skipped rules | DONE |
| `RuntimeConsistencyValidatorTests` timeout case | timeout at fake-clock deadline | arbitrary scheduler-yield limit could fail under four-core suite load | keep / Unit | fake time + fake HTTP | await request-start signal, then advance fake time | DONE |
| CLI command files | command/help/output/error contract | CLI product surface | keep / Spec | command + fake HTTP | moved to `Mohist.Cli.SpecTests` | DONE |
| CLI parser/renderer/updater/probe/validator files | cohesive implementation contracts | technical behavior independent of command route | keep / Unit | direct service | moved and renamed `*Tests` | DONE |
| `OtelExecutionChainTracingSpecs`, `OtelOutboundHttpTracingSpecs`, `OtelSourceSubscriptionSpecs`, `OtelInboundHttpTracingSpecs`, `OtelExporterFailureIsolationSpecs` | `ConfigureTracing` technical registration; `OtelSelfFeedbackTests` and `OtelSignalRTracingSpecs` retain product-visible telemetry behavior | source registration and provider configuration, not a synthetic listener or TestServer instrumentation topology | delete + rewrite / Unit + Spec | direct source/listener + TestServer | deleted synthetic listener/exporter cases; `MohistOpenTelemetryRegistrationTests` records SignalR and EF activities in the production builder | DONE |
| `OtlpRoutesIntegrationSpecs`, `OtelQueryRoutesIntegrationSpecs` | OTLP ingestion and trace-query route contracts | clients can write and read telemetry through the public route surface | keep / Spec | HTTP host | renamed to `OtlpRoutesSpecs` and `OtelQueryRoutesSpecs`; no behavior moved | DONE |
| `HttpApiJsonWiringSpecs` methods | `ConfigureMohistServices` JSON-option registration; `JSONTests` owns shared serializer behavior | HTTP/SignalR option binding is a technical registration contract; raw Unicode HTTP cases repeat serializer coverage | delete + rewrite / Unit | DI + HTTP host | moved four registration cases to `MohistServiceRegistrationJsonTests`; deleted two duplicate HTTP cases | DONE |
| `AgentSessionGrainRecoverySpecs` and weak `AgentSessionRecoveryApiSpecs` methods | compact/reset route contract, `AgentSessionRecoveryDomainTests`, `AgentSessionGrainPersistenceTests`, `IssueSessionApiSpecs` | public recovery results, technical persistence failure and usage projection already have focused owners | delete / none | full host + direct grain + DB | deleted nine direct-grain duplicates and three empty or duplicate API cases | DONE |
| direct `ResolveGenericFollowupTargetAsync`, `ResolveGenericCancelTargetAsync`, `ResolveFollowupTargetAsync` methods | generic/issue followup and cancel route specs | route outcomes already prove runner binding, active/inactive, terminal, unknown and project-isolation decisions, including SignalR payloads | delete / none | full host + direct querier | deleted eight resolver mirrors; public HTTP specs remain the only product owner | DONE |
| `AgentSessionContextExhaustionSpecs` methods | `ContextExhaustionClassifierTests`, `ContextHealthClassifierTests`, `AgentSessionRecoveryDomainTests`, session metadata API and transcript-publisher specs | classifier thresholds/limiting, domain event shape, public metadata and realtime event delivery each have a focused owner | delete / none | full host + direct grain + DB | deleted five repeated grain paths, including weak metadata and event-presence assertions | DONE |
| duplicated `AgentSessionLifecycleDedupSpecs` methods | retained lifecycle/channel cases in the same Spec and `AgentSessionTransactionalEventAppendTests` | one attached runtime-bound event, transcript-only raw events and lifecycle envelope persistence each need one strongest owner, not status-by-status copies | collapse / Spec + Unit | full host + event store | removed attach duplicate, three terminal-status copies and liveness duplicate; retained six distinct channel/lifecycle risks | DONE |
| `RuntimeEntrySpecs.AgentStatus_WhenNoRunnerConnected_ReportsUnavailableRuntime` | `AgentStatusResponse.Create` response assembly | no host, route or user flow is driven; the response factory's unavailable-state mapping is an independent technical contract | reclassify / Unit | direct | moved to `AgentStatusResponseTests` | DONE |
| `CliNotifySetupCommandSpecs.ProbeHermesHealthAsync_NonAbsoluteHealthBase_ReturnsUnhealthy` | `NotifyCommands.ProbeHermesHealthAsync` URL validation | invalid health-base handling is a direct helper contract; CLI setup specs retain user-visible abort/output behavior | reclassify / Unit | direct | moved to `NotifyCommandsTests` | DONE |
| `AgentSessionSpecs.RunnerAppendsSessionEvents_StoresAggregateDomainEvents` | retained lifecycle Spec and `AgentSessionTransactionalEventAppendTests` | usage-recorded type is already tested through the runner path; source/subject stamping is already tested transactionally | delete / none | full host + event store | deleted overlapping combined assertion | DONE |
| `CliNotifySetupCommandSpecs` fixture setup/teardown | `FakeFileSystem` is the command's only file dependency | real temporary directory creation/deletion contributes no command behavior and leaks an external dependency | simplify / Spec | fake filesystem | keep an absolute fake path but remove all real directory operations | DONE |
| `RuntimeEntrySpecs.AgentStatus_WhenRunnerUnregistered_HeartbeatRefreshesInfoButPollRestoresPresence` | `RunnerFailureTests.HeartbeatRepair_OfflineGrain_RefreshesInfoButPollPresenceRestoresOnline` and `RunnerGrainTimeProviderTests.Heartbeat_DoesNotRefreshPresence_ButPollPresenceDoes` | the HTTP test observes only runner grain/registry state, not an API result; the direct technical owners cover offline info refresh and poll-owned presence | delete / none | full host + grain | deleted the duplicate technical path | DONE |
| `RunnerHeartbeatConnectionApiSpecs` 8 methods | public `GET /api/runner/identity` connection state; `RunnerConnectionTracker` remains an internal transport detail | a runner that reports a connection remains publicly connected after its later info-only heartbeat | collapse + rewrite / Spec | HTTP host | replaced tracker mirrors with one `RunnerIdentityConnectionSpecs` API contract | DONE |
| `RuntimeEntrySpecs` runtime seed time | `MohistIntegrationFixture.TimeProvider` | workflow setup must not add an untracked wall-clock dependency | simplify / Spec | fake time | replaced `DateTimeOffset.UtcNow` with fixture time | DONE |
| session activity/read/context Specs, `InboxApiSpecs` and `IssueFeedbackApiSpecs` seed times | `MohistIntegrationFixture.TimeProvider` | ordering, lifecycle and feedback data must be deterministic under any machine clock | simplify / Spec | fake time | replaced local wall-clock construction with fixture time; removed unused inbox polling helper | DONE |
| `AttachmentApiSpecs` HTTP lifecycle methods | attachment upload/bind/download/remove, project isolation and API limit contracts | users can attach content to issues/comments and receive correct lifecycle/errors | keep / Spec | HTTP host | retained six API cases | DONE |
| `AttachmentApiSpecs.UploadAsync_RejectsStreamThatExceedsDeclaredSizeLimit`, `CleanupExpiredPending_RemovesRowsAndStoredContent` | `AttachmentService` size-normalization and pending-cleanup implementation contracts | a malformed declared length cannot leave data, and expired pending attachment content/rows are removed | reclassify / Unit | SQLite + storage adapter | moved to `AttachmentServiceTests` with fixed time | DONE |
| direct wall-clock test seeds and default test clocks | `design/testing.md` deterministic-time rule | source data, fixtures and host setup cannot vary by machine clock | simplify / Spec + Unit | fake time / fixed data | replaced direct wall-clock calls across all five test projects; grain defaults now use a fake clock | DONE |
| `RuntimeBuildInfoTests.MetadataIdentity_WhenAssemblyHasInformationalVersion_ReturnsVersionAndGitHash`, `GitHash_WhenInitialized_RemainsStableForProcessLifetime` | `ResolveIdentity` Unit tests own version/hash fallback behavior | current assembly, repository and process environment are test-run inputs rather than an independent product or implementation contract | delete / none | process environment + source tree | deleted two build-environment mirrors | DONE |
| `RuntimeBuildInfo` startup time and Git HEAD parser | `IRuntimeBuildInfo.StartedAt` and identity fallback implementation | startup metadata must be deterministic in tests; detached and symbolic Git HEAD parsing must not require a real repository | keep + rewrite / Unit | fake clock + fake filesystem | injected `TimeProvider`; parser now accepts `IFileSystem` | DONE |
| `NoActiveProjectMessageSourceAlignmentTests` 3 methods | `NoActiveProjectMessageTests` and CLI command/API output tests | the diagnostic must be emitted to users, not merely referenced from particular source files | delete / none | source text | deleted three source-shape mirrors | DONE |
| `SystemUpdateServiceInvariantTests.SourceAudit_*` 7 methods | `SystemUpdateServiceRecoveryTests`, `SystemUpdateServiceReconnectTests`, `SystemUpdateServiceStatusTests` and `SystemUpdateServiceOutcomeTests` | failed state, log cap, persistence, lock release and enabled/disabled outcomes are covered through service behavior | delete / none | source text | deleted brittle source-layout scans; retained `PersistTransitionAsync_ReleasesLockOnlyAfterSave` | DONE |
| `SystemUpdateRecoveryTests.SourceAudit_*` 2 methods | stale/fresh/reconnect/retry recovery behavior driven by fake time and `FakeProcessStartTimeProvider` | production source layout and exact use of process APIs are not a product, technical-result or documented architecture contract | delete / none | source text | deleted two source-layout scans | DONE |
| `ProjectIsolationIntegrationTests` 6 methods and `SystemSpecs.EventBridgeTests` 2 methods | `UserNotificationDispatcherProjectFilterTests` and `Events.EventBridgeTests` | filter branches and envelope delivery each need one owner; repeated bridge paths do not add a distinct risk | collapse / Unit | direct dispatcher + fake hub | retained one project-scoped EventBridge delivery case; deleted eight duplicates and the `Integration` test name | DONE |
| `SkillsCliRuntimeTests.InvokeSkillsAsync` setup | `SkillsCliRuntimeTests` composition contract | a read-only command must never accidentally invoke a process or HTTP request | simplify / Unit | rejecting fake command + HTTP handler | replaced real executor/default HTTP handler with fail-fast fakes | DONE |

执行时覆盖全部 Server 与 CLI test methods；只有同一来源、风险和动作的方法组可以合并，
不允许只完成预填项或只按 file/class 归类。

### Deleted test mapping

| Removed test | Retained owner or deletion reason | State |
|---|---|---|
| `RunnerWorkflowTerminalStatusHandlerSpecs` (3) | `RunnerWorkflowStatusRouterSpecs` owns all three routed outcomes | DONE |
| `AgentSessionEventsMigrationSpecs` source-text methods (2) | migrated schema assertions execute the real migrations | DONE |
| migrated-service registration matrix (112) | feature tests and the smaller scanner UnitTests own observable registration behavior | DONE |
| `OtelServiceRegistrationSpecs` (3) | `OtelDbTests` and OTLP route specs own database/route behavior | DONE |
| `SourceCodeUpdaterStructureTests` and API prelude source-shape methods (5) | behavior tests own updater/API results; source layout is not a contract | DONE |
| `UnitTests_MustNotReferenceHostingPackages`, `ComponentSpecs_MustNotReferenceMvcTesting` (2) | runtime mechanism does not define a test kind | DONE |
| `DomainInternalLayers_ShouldBeFreeOfCycles` (1) | exceptions left only an undocumented partial rule | DONE |
| empty activity limit/default waiting cases (2) | no user-observable assertion or active caller contract | DONE |
| empty trigger-label map case (1) | no caller or distinct product behavior beyond manual launch | DONE |
| duplicate Agent cost cases (4) | owned by existing cost/usage cases, or only re-computed a returned value | DONE |
| lifecycle unknown-issue and database-path cases (2) | completion handler / path contract and workspace API specs own the risk | DONE |
| `IssueWorkflowLifecycleSpecs` 12 methods | two start cases were rewritten as API specs; the other ten are owned by product loop, issue creation, profile API, start readiness, path contract, completion handler or workflow-control specs; unknown legacy failure values have a focused JSON Unit test | DONE |
| `IssueRepositoryReferenceSpecs` (6) | repository API covers creation/read behavior, workflow and workspace specs use live configuration, resolver Unit tests cover pure problem classification; direct persistence shape has no standalone contract | DONE |
| `IssueRepositoryResolutionRegressionSpecs` (19) | one list response case moved to `IssueRepositoryApiSpecs`, one rebase task-input case moved to `IssueRebaseRecoveryTests`; all other cases are covered by repository API, workflow/workspace resolution, resolver Unit tests, or assert forced internal state | DONE |
| `IssueWorkflowRepositoryResolutionSpecs` removed cases (2) | removed-repository start became an HTTP `IssueCreationSpecs` case; ghost repository reference is uncreatable and resolver Unit tests own its classification | DONE |
| `IssueCreationSpecs` duplicated/shape methods (4) | initial creation case covers default profile and querier; `IssueVariableBuilderTests` covers the emitted change path; duplicate grain creation has no public contract | DONE |
| `IssueCreationSpecs` direct prerequisite methods (10) | HTTP create/prerequisite specs cover every user outcome; `IssueStartReadinessDomainTests` covers start-gate logic | DONE |
| `IssueCreationSpecs` direct risk methods (4) | one create/read API case protects product visibility; `IssueDomainTests` owns allowed values, null and persistence | DONE |
| three skipped internal-cycle rules | skip has no protection and no ratchet/design decision exists | DONE |
| synthetic OTel source/listener/exporter cases | `MohistOpenTelemetryRegistrationTests`, `OtelSelfFeedbackTests` and `OtelSignalRTracingSpecs` own production registration and observable tracing behavior | DONE |
| `HttpApiJsonWiringSpecs` (6) | `MohistServiceRegistrationJsonTests` owns JSON/SignalR registration; `JSONTests` owns serializer behavior | DONE |
| `AgentSessionGrainRecoverySpecs` (9) and weak recovery API cases (3) | compact/reset API specs, recovery domain/persistence unit tests and session read API specs own each retained risk | DONE |
| direct followup/cancel resolver mirrors (8) | public followup/cancel API specs cover the same route decisions and runner payloads | DONE |
| `AgentSessionContextExhaustionSpecs` (5) | classifier/domain Unit tests, session metadata API and transcript publisher specs cover thresholds, event shape and user-visible results | DONE |
| duplicate `AgentSessionLifecycleDedupSpecs` cases (5) | retained attach, transcript-channel and terminal-channel cases plus transactional lifecycle-event Unit tests | DONE |
| `AgentSessionSpecs.RunnerAppendsSessionEvents_StoresAggregateDomainEvents` (1) | retained lifecycle Spec and transactional lifecycle-event Unit tests | DONE |
| nine low-signal Arch rules (`DataStores`, feature directory, grain name/location, EF entity name, domain-module dependency, Spec public/namespace) | no stable design source; the store rule only proved a Store exists and the domain rule used bidirectional exceptions | DONE |
| `RuntimeEntrySpecs.AgentStatus_WhenRunnerUnregistered_HeartbeatRefreshesInfoButPollRestoresPresence` (1) | direct runner Unit tests own the heartbeat/poll presence invariant | DONE |
| seven `RunnerHeartbeatConnectionApiSpecs` tracker variants | `RunnerIdentityConnectionSpecs` keeps the public connected-state contract; null/empty/overwrite tracker branches are not independent product cases | DONE |
| `RuntimeBuildInfoTests.MetadataIdentity_WhenAssemblyHasInformationalVersion_ReturnsVersionAndGitHash`, `GitHash_WhenInitialized_RemainsStableForProcessLifetime` (2) | assembly/repository/process identity is an environment mirror; pure `ResolveIdentity` tests and injected-clock construction own the implementation contract | DONE |
| `NoActiveProjectMessageSourceAlignmentTests` (3) | direct command/API tests assert the shared diagnostic; source references are not a contract | DONE |
| `SystemUpdateServiceInvariantTests.SourceAudit_*` (7) | service behavior tests own failed status, log cap, persistence/lock and enablement outcomes; source layout is not a contract | DONE |
| `SystemUpdateRecoveryTests.SourceAudit_*` (2) | fake-clock and fake-process-start recovery behavior tests own stale/reconnect/retry outcomes; source layout is not a contract | DONE |
| `ProjectIsolationIntegrationTests` (6) and `SystemSpecs.EventBridgeTests` (2) | one retained `Events.EventBridgeTests` project-routing case, dispatcher filter Unit tests and existing EventBridge envelope tests own the risks | DONE |

Each deliberate deletion above has a retained owner or a stated no-value reason. The final
discovery record replaces historical subtotal claims after the remaining audit batches.

### Verification record

| Gate | Result |
| Baseline discovery/pass/skip | 5013 total: 5001 passed (Unit 1385; Component 1678; Integration 1041; Arch 32; CLI 865) and 12 skipped (Integration 9; Arch 3) |
| Server SpecTests | 898 passed, 0 skipped |
| Server UnitTests | 2960 passed, 0 skipped |
| Server ArchTests | 20 passed, 0 skipped |
| CLI SpecTests | 721 passed, 0 skipped |
| CLI UnitTests | 139 passed, 0 skipped |
| Current total/pass/skip | 4738 total: 4738 passed, 0 skipped; versus baseline, 263 fewer executed cases and 12 fewer skips |
| Normalized discovery | 4729 method identities, no duplicate display names. `RepoSubcommands_AcceptProjectAndProjectId` has ten `MemberData` rows, so its one discovery identity accounts for the nine-case difference from the executed total |
| Full solution | `dotnet test Mohist.sln -p:SkipWebBuild=true --no-build` passed three times: 4738/4738, 0 skipped |
| Four-core timing twice | baseline 30.84s / 30.92s (median 30.88s); current 27.08s / 27.05s (median 27.07s, 12.4% faster) |
| Baseline stability | one initial baseline run hit the old `RuntimeConsistencyValidatorSpecs` scheduler-yield failure; two later samples passed. The current test awaits a request signal instead. |
| Structural searches | old project names, orderers/traits/shards, all skip attributes, test-project references and TestSupport test dependencies all empty; no linked test source |
| `git diff --check` | staged and unstaged checks empty |
| Scope | product behavior unchanged; source changes are test layout, testability seams, test code and test documentation |
