## Context

Mohist server 的全部 DI 注册集中在 `MohistServiceRegistration.ConfigureMohistServices`（`packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs`），目前约 80 条手写 `AddScoped`/`AddSingleton`。该入口被两条路径复用：

- **生产**：`Program.cs` → `AddMohistServerCore` → `ConfigureMohistServices`。
- **测试**：`MohistDbFixture` 等直接调用 `ConfigureMohistServices`，以镜像生产注册（文件头注释明确要求“新增服务必须加在此处，否则 fixture 会与生产漂移”）。

现有注册可清晰分成两类：

- **可直接扫描的 concrete 自注册**（约 33 条）：实现是无参或仅依赖 DI 可解析类型，以自身类型注册，无接口映射、无工厂、无配置绑定。例如 `IssueQuerier`(Scoped)、`ProjectQuerier`(Singleton)、`LabelCatalogService`、`WorkflowDispatchBuilder`、`RunnerStatusService` 等。
- **必须保留手写的特殊注册**（约 25 条 + 框架调用）：接口→实现映射（`IEventStore→EventStore`、`IPromptLoader→FilePromptLoader`…）、工厂委托（`IGitService`、`IRuntimeBuildInfo`、`IStateStore<AgentSession>`）、配置绑定（`WorkflowArtifactStorageOptions`、`AgentJobOptions`）、`AddHttpClient<ISystemReadinessProbe,…>`、`AddHostedService`、`AddCloudEventBus`/`AddSignalR` 等。

约束（来自 issue 与 `design/architecture.md`）：不替换 DI 容器（仍用 `Microsoft.Extensions.DependencyInjection`）；不强制改变接口/实现命名；可分模块逐步迁移；数据模型与对外 API 不变。

## Goals / Non-Goals

**Goals:**

- 用程序集扫描消除“concrete 自注册”这一类样板，并从机制上杜绝漏注册（生产与测试同步受益）。
- 保留现有手写注册不被破坏，扫描与手写共存且手写优先。
- 约定集中、可发现、可演进。

**Non-Goals:**

- 不替换为 Autofac 等容器，不引入装饰器用法（issue 提到但不在此处启用）。
- 不迁移接口→实现映射、工厂委托、配置绑定、`HttpClient`、`HostedService` 等特殊注册。
- 不一次性全量替换；不在本次改变任何既有服务的解析类型或生命周期。

## Decisions

### 决策 1：约定载体 = 标记接口（`IScopedService` / `ISingletonService`），而非命名或特性

扫描只认标记接口：`IScopedService` → Scoped，`ISingletonService` → Singleton。两者为空标记接口（marker），放在 `Mohist.Server.Infrastructure.Hosting` 命名空间。

- **理由**：opt-in、显式，生命周期直接编码进接口名；不强制任何命名约定（满足 issue non-goal）；编译期即可看出某类型被自动注册。
- **考虑过的替代方案**：
  - *命名约定*（如 `*Querier` → Scoped）：被 issue 显式排除（不强制改命名），且现有单例/作用域混用同名后缀，无法可靠推断生命周期。**否决**。
  - *特性*（`[ScopedService]`/Scrutor 内建 `[ServiceDescriptor]`）：需在每个类型上加特性引用，可发现性不如接口，且与领域层耦合。**否决**（保留为后续可选）。
- **Transient 暂不提供**：现有代码无 Transient 注册，按“数据模型尽量简洁”原则只暴露两个标记。

### 决策 2：扫描以 `AsSelf()` 注册，仅覆盖 concrete 自注册

Scrutor `Scan` 仅 `AddClasses(c => c.AssignableTo<IScopedService>()).AsSelf().WithScopedLifetime()`（Singleton 同理）。不使用 `AsMatchingInterface()`。

- **理由**：候选集（约 33 条）当前全部以 concrete 类型注册并被消费；`AsSelf()` 行为与现状逐字一致，零行为变更风险。接口→实现映射保持手写，满足 spec“多接口映射保留手写”。
- **考虑过的替代方案**：`AsMatchingInterface()`（`FooService` 自动注册为 `IFooService`）可进一步减少接口映射样板，但会改变解析键、影响范围大且非本次目标。**否决**（列为 Open Question，待 concrete 迁移完成后评估）。

### 决策 3：注册顺序 = 先扫描、后手写，靠“后注册者生效”保证手写优先

在 `ConfigureMohistServices` 顶部调用 `services.AddMohistConventionalServices()`（封装扫描），随后才是现有的手写注册与框架调用。

- **理由**：`Microsoft.Extensions.DependencyInjection` 对同一服务类型多次注册时，`GetService` 返回**最后注册**的实现。先扫描后手写 ⇒ 手写天然覆盖扫描结果；且对同一 concrete 多次 `Add` 不抛异常，满足 spec“显式注册优先且不冲突 / 不抛重复注册错误”。
- **考虑过的替代方案**：用 Scrutor `Replace()` 或在扫描前剔除手写集合——复杂且易错；后注册者生效是 MS DI 的既定语义，最简单可靠。**否决**。

### 决策 4：扫描范围 = 单一 server 程序集，与现有 `AddCloudEventHandlersFromAssembly` 一致

扫描 `typeof(MohistServiceRegistration).Assembly`。生产与测试走同一 `ConfigureMohistServices` 入口 ⇒ 同一扫描范围，满足 spec“生产与测试注册一致性”。

### 决策 5：分模块渐进迁移，迁移一个即删除一条手写

迁移按域分批（如 Issue → Workflow → Runner/System），每批：给 concrete 服务加标记接口 → 删除其手写行 → 跑 `packages/server` 全量测试。接口/工厂/配置/HttpClient/HostedService 行原样保留。迁移完成后 `ConfigureMohistServices` 只剩特殊注册与框架调用。

## Risks / Trade-offs

- **[标记接口加错生命周期] → 类型以错误生命周期解析。** 缓解：一个类型同时实现两个标记接口时扫描/启动期 fail-fast；新增测试断言每个已迁移类型的关键字为预期生命周期。
- **[迁移期同一 concrete 残留手写行 + 扫描] → 双重注册。** 后注册者生效属良性，但会掩盖“忘记删手写”。缓解：迁移某类型即删其手写行；review 时确认。
- **[扫描到构造函数含非 DI 可解析参数的类型] → 运行期解析爆炸。** 缓解：标记是 opt-in，候选集均已在今日以手写正常解析（构造函数已 DI 可解析），故无新风险；新服务作者按标记约定即可。
- **[开放泛型类型] → `AsSelf()` 简单扫描不支持开放泛型注册。** 当前候选集无开放泛型，无影响；若未来出现则保留手写。
- **[新增对外依赖 Scrutor] → 供应链与升级。** 缓解：Scrutor 为成熟、广泛使用的 MS DI 扩展，版本随 .NET 升级；仅用其 `Scan` 子集，锁定主版本。

## Migration Plan

1. **加包**：`packages/server/src/Mohist.Server` 引入 `Scrutor`。
2. **加标记**：在 `Infrastructure/Hosting` 新增 `IScopedService.cs`、`ISingletonService.cs`（空标记接口）。
3. **加扫描入口**：新增 `ServiceCollectionExtensions.AddMohistConventionalServices(this IServiceCollection)`，内部 `Scan` 两段（Scoped/Singleton，`AsSelf()`）。
4. **接线**：在 `ConfigureMohistServices` 最顶部调用 `AddMohistConventionalServices()`（先于一切手写注册）。
5. **逐域迁移**：按 Issue → Workflow → Runner/System 顺序，给对应 concrete 服务加标记并删手写行，每批后跑 `npm test`（server）。
6. **校验一致性**：保留/新增测试，断言 `MohistDbFixture` 与生产对若干代表性服务（`IssueQuerier`、`ProjectQuerier` 等）解析出相同实现与生命周期。

**回滚**：单 commit 即可回滚（移除扫描调用，标记接口变惰性、无副作用）。若已迁移部分出问题，可仅回滚对应域的迁移 commit，手写注册恢复。

## Open Questions

- 迁移完成后是否引入 `AsMatchingInterface()` 以一并减少接口→实现映射的样板？（待 concrete 迁移完成、量化收益后再定。）
- 是否需要一条“架构测试”断言“实现标记接口的每个 concrete 都被注册且仅注册一次”，作为防回归护栏？
- `AddHostedService` 类型（`IssueWorkflowReconciliationService`、`AttachmentCleanupService`）是否值得引入独立 `IHostedService` 扫描？（当前倾向：否，保持手写，行为不同。）
