## ADDED Requirements

### Requirement: 约定式自动注册

Server 启动注册时 SHALL 通过程序集扫描，将符合既定约定（接口、命名或标记）的服务类型自动注册到 DI 容器，而无需为每个符合约定的服务手写注册语句。约定的具体定义（接口/命名/生命周期映射）SHALL 集中在一处维护。

#### Scenario: 新增符合约定的服务被自动发现

- **WHEN** 开发者新增一个符合注册约定（接口/命名/标记）的服务类型，且未在任何注册代码中显式列出
- **THEN** server 启动后该服务 SHALL 可从 DI 容器成功解析
- **AND** 复用同一注册入口的测试 fixture（如 `MohistDbFixture`）SHALL 同样能解析该服务

#### Scenario: 不符合约定的服务不被自动注册

- **WHEN** 一个服务类型不匹配任何注册约定（接口、命名、标记均不符合）
- **THEN** 程序集扫描 SHALL NOT 注册该服务
- **AND** 该服务仅在显式手写注册后才可被解析

### Requirement: 保留原生 DI 容器

约定式注册 SHALL 以 `Microsoft.Extensions.DependencyInjection` 作为唯一 DI 容器，通过该容器的程序集扫描扩展实现，而 NOT 替换为 Autofac 等第三方容器。

#### Scenario: 容器实现类型不变

- **WHEN** 注册流程执行完成
- **THEN** 容器 SHALL 仍是 `Microsoft.Extensions.DependencyInjection` 提供的实现
- **AND** 所有现有服务的解析方式 SHALL 保持不变

### Requirement: 特殊注册保留显式手写

需要工厂委托、外部构造参数、`IConfiguration` 选项绑定、`HttpClient` 配置、多接口映射或生命周期覆盖的服务 SHALL 通过显式手写注册，而 NOT 纳入自动扫描。

#### Scenario: 工厂委托与外部参数的服务保留手写

- **WHEN** 一个服务需要通过工厂委托构造或依赖外部参数（例如 `IGitService` 依赖 `runnerRoot`，或 `IRuntimeBuildInfo` 转发到 `RuntimeBuildInfo`）
- **THEN** 该服务 SHALL 保持显式手写注册
- **AND** 扫描 SHALL NOT 为其产生冲突的重复注册

#### Scenario: 配置绑定与多接口映射的服务保留手写

- **WHEN** 一个服务依赖 `IConfiguration` 绑定的选项（如 `WorkflowArtifactStorageOptions`、`AttachmentStorageOptions`、`AgentJobOptions`）、需要 `AddHttpClient` 配置，或一个实现映射到多个服务接口（如 `IAgentSessionStore` 与 `IStateStore<AgentSession>`）
- **THEN** 该服务及其选项/映射 SHALL 保持显式手写注册
- **AND** 扫描 SHALL NOT 重复注册

### Requirement: 显式注册优先且不冲突

当同一服务类型既被扫描约定覆盖又被显式手写注册时，显式注册 SHALL 优先生效，且注册过程 SHALL NOT 因重复注册而抛出异常或导致启动失败。

#### Scenario: 显式注册与扫描共存

- **WHEN** 一个服务同时匹配扫描约定并存在显式手写注册
- **THEN** 最终生效的注册 SHALL 为显式手写注册（含其指定的实现与生命周期）
- **AND** server 启动 SHALL NOT 因重复注册而失败

### Requirement: 生产与测试注册一致性

约定式扫描 SHALL 在生产启动入口与测试 fixture 注册入口共享同一套约定与扫描范围，使两端解析出的服务集合一致。

#### Scenario: 测试 fixture 与生产解析一致

- **WHEN** 测试 fixture（如 `MohistDbFixture`）调用共享注册入口
- **THEN** fixture 解析出的符合约定的服务集合 SHALL 与生产启动时一致
- **AND** 生产新增的符合约定的服务 SHALL NOT 要求在 fixture 中额外补注册即可被解析

### Requirement: 迁移不改变既有服务行为

将既有手写注册迁移为扫描注册 SHALL NOT 改变这些服务的解析类型或生命周期（Scoped/Singleton/Transient）。

#### Scenario: 既有服务生命周期与解析类型保持

- **WHEN** 一个既有手写注册服务（例如 Scoped 的 `IssueQuerier`、Singleton 的 `ProjectQuerier`）被迁移为扫描注册
- **THEN** 迁移后该服务解析出的实现类型 SHALL 与迁移前一致
- **AND** 该服务的生命周期 SHALL 与迁移前一致
