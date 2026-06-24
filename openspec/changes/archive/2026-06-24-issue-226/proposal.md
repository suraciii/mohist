## Why

Mohist server 注册依赖注入（DI）的服务全部是手写的，目前集中在 `MohistServiceRegistration.ConfigureMohistServices` 中（约 80 行逐条 `AddScoped`/`AddSingleton`）。随着服务增多，这种手写方式产生大量样板代码、新增服务时容易漏注册（只在运行时才暴露），且注册逻辑无法随各模块就近维护。引入基于程序集扫描的约定式注册可以从机制上消除漏注册风险，让新增服务默认被自动发现。

## What Changes

- 引入 [Scrutor](https://github.com/khellang/Scrutor) 作为 `Microsoft.Extensions.DependencyInjection` 的扫描扩展，仍以原生 DI 容器为基础（不替换为 Autofac 等容器）。
- 定义服务自动注册约定：实现特定接口（如 `*Querier`、`*Resolver`、`*Service` 等查询/解析/服务类型）或带标记的服务按约定生命周期（Scoped/Singleton）被程序集扫描自动注册。
- 将 `MohistServiceRegistration.ConfigureMohistServices` 中符合约定的手写注册迁移为扫描注册，需工厂委托、外部参数（如 `runnerRoot`、`configuration` 绑定）、多接口映射或显式生命周期覆盖的注册保留为手写，与扫描注册共存。
- 新增服务时，符合约定的服务默认无需手动注册即可被生产端和测试 fixture（`MohistDbFixture` 等）同时发现，避免两者漂移。
- 分模块逐步迁移，不要求一次性全量替换，也不强制改变现有接口/实现命名。

## Capabilities

### New Capabilities

- `service-registration`: 约定式依赖注入注册。规定 server 启动时如何通过程序集扫描按接口/命名约定与生命周期策略自动发现并注册服务，以及哪些服务仍需显式手写注册（工厂委托、配置绑定、多接口映射、生命周期覆盖），并要求生产注册与测试 fixture 注册共享同一套约定以免漂移。

### Modified Capabilities

- 无。本次为内部注册机制重构，已注册服务的行为与生命周期不变，不涉及任何既有 spec 的需求级行为变更。

## Impact

- 新增 NuGet 依赖 `Scrutor`（server 项目 `packages/server/src/Mohist.Server`）。
- 改动 `packages/server/src/Mohist.Server/Infrastructure/Hosting/MohistServiceRegistration.cs`：新增扫描注册入口，迁移符合约定的手写注册。
- 可能新增轻量注册约定/标记（如 marker interface 或 attribute）及其放置规则，需遵循 `design/architecture.md` 的边界约定。
- 影响 `packages/server/tests` 中复用 `ConfigureMohistServices` 的测试 fixture：需验证扫描在生产与测试程序集下均生效，避免漏注册或重复注册。
- 不涉及对外 HTTP API、数据模型或存储格式的变化；无 **BREAKING** 的用户可见行为。
