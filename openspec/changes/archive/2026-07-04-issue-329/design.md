# Design: 收敛 CLI 消费层：消除重复前奏与 HTTP 样板

## Context

CLI 的消费层把同一逻辑复制了十几份。代码勘察确认了三处独立的重复源：

1. **命令前奏**（输出模式校验 + 项目引用解析）。`ValidateOutput(MohistCliApi, string?) -> (string Mode, int Exit)` 在 `IssueCommands`（`MohistCliCommands.Issue.cs:61`）与 `EpicCommands`（`MohistCliCommands.Epic.cs:38`）各有一份字节相同的定义，被 33 处调用。`ResolveProjectId(...)` 包装只存在于 `IssueCommands`（`Issue.cs:72`，29 处调用），而 `Epic/Agent/Workflow/ProjectWorkflow/Repository` 等 9 个命令类各自内联调用 `api.ResolveProjectIdAsync(...)`——同语义的三种写法。此外 27 处直接内联 `MohistCliApi.ValidateOutputMode(...)` + 手写 `Invalid` 分支（Agent ×8、ProjectWorkflow ×9 等）。

2. **HTTP/envelope 层**（`MohistCliApi.cs`，1105 行）。按动词分的 5 个 `Print*Async` 都汇入私有 `PrintResponseAsync`；5 个 `*WithOutputAsync` 都汇入私有 `PrintEnvelopeAsync`——两条汇但请求构造各抄一份。envelope 字段提取块 `node["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode` → `error`/`code` 在 `PrintResponseAsync`/`PrintRawResponseAsync`/`ReadPostResultAsync`/`ReadSuccessDataAsync`/`PrintProjectListAsync`/`PrintSystemInfoAsync` 重复 6 处，外加 `MohistCliCommands.Agent.cs:860` 与 `MohistCliCommands.Otel.cs:181` 各一份外部重写。`404 → exit 4` 映射散落 13 处。

3. **Server 配置 legacy `model` fallback**（`ConfigService.cs`）。`GetAgentConfigAsync`（L139–148）从废弃的单字段 `model` 合成 agent 配置；`SetAgentModelAsync`（L215）为此保留 `ClearAsync("model")`；schema（L27）仍登记 `model`。统一 `agent` 对象引入后该路径已被取代。

约束：项目声明无需版本兼容。Non-Goals 明确不重组命令树、不动表格渲染器、不改对外行为（legacy fallback 移除是唯一例外）。命令类（`IssueCommands`/`EpicCommands`/`AgentCommands`…）是彼此独立的 `static` 类，各自 `Build(api)` 并在 handler 里收到同一个 `MohistCliApi api` 实例——这点决定了共享 helper 的落点。

## Goals / Non-Goals

**Goals:**
- 输出模式校验与项目引用解析的前奏各只保留一份实现，所有命令类共享，退出码与模式语义不变。
- 5 个按动词的 HTTP 方法合并为单一通用请求路径（verb 方法可退化为薄转发）。
- envelope 的 `success`/`error`/`code` 提取与 `404→4` 映射各收口到单一实现。
- 移除 `ConfigService` 的 legacy `model` fallback、schema 条目与 `ClearAsync("model")`。
- 既有 CLI/server spec 全绿，无回归。

**Non-Goals:**
- 不拆分/合并命令 partial 文件，不重组命令树。
- 不重组 API 层职责边界，不改任何 HTTP 契约或 CLI 输出格式。
- 不动表格渲染器。
- 不为 legacy `model` 提供迁移垫片（无版本兼容要求）。

## Decisions

### D1. 前奏 helper 落在 `MohistCliApi` 实例方法上
新增 `api.ResolveOutputMode(string?) -> (string Mode, int Exit)` 与 `api.ResolveProject(string?, string?) -> Task<(string ProjectId, int Exit)>`，二者分别包装既有的静态 `ValidateOutputMode` 与实例 `ResolveProjectIdAsync`，把 `OutputModeResult`/`null` 转成 `(mode, exit)` 元组。`api` 已被线程进每个命令 handler，且底层校验/解析本就住在 `MohistCliApi`，包装器是其自然邻居。

删除 `IssueCommands.ValidateOutput`、`EpicCommands.ValidateOutput`、`IssueCommands.ResolveProjectId` 三份重复定义；33 处 `ValidateOutput(api, o)` 改 `api.ResolveOutputMode(o)`，29 处 `ResolveProjectId(api, …)` 改 `api.ResolveProject(…)`，27 处内联 `ValidateOutputMode + Invalid` 分支与 9 类内联 `ResolveProjectIdAsync + null` 检查统一改调这两个方法。

**Alternatives:**
- (a) 放到共享 hub `MohistCliCommands` 上作 `static`：被否——命令类是独立 static 类，调用点会变成 `MohistCliCommands.ResolveOutputMode(api, …)`，引入今天不存在的跨类耦合，且 `MohistCliCommands` 现在是组合根 + 选项定义，混入运行时前奏会模糊职责。
- (b) 新建 `CommandPrelude` static 类：被否——两个 6 行 helper 不值得新增类型。

### D2. HTTP 请求收口为单一通用发送方法
新增私有 `SendAsync(HttpMethod method, string path, object? body) -> Task<HttpResponseMessage>`：统一构造请求（PATCH 走 `JsonContent.Create` 的 `HttpRequestMessage` 路径在此内化），并集中 `HttpRequestException → ServerUnavailableMessage + exit 1` 处理。5 个 `Print*Async` 与 5 个 `*WithOutputAsync` 退化为单行薄转发（spec 明确允许）。请求构造与“server 不可达”处理各只剩一份。

**网络异常一致性说明（行为微调）：** 今天裸 `PrintGetAsync/PostAsync/...` 不捕获 `HttpRequestException`（会向上抛），只有 `*WithOutputAsync` 与 `*SafeAsync` 捕获。统一进通用发送方法后，裸方法也会优雅退出 1。这符合 spec 的“网络失败打印 server-unavailable”场景，且“崩溃 → 优雅 1”是严格改善；但属可观察变化，需由 spec 覆盖确认（见 Risks）。

**Alternatives:**
- 请求工厂 `Func<HttpClient, Task<HttpResponseMessage>>`：被否——比 method+body 重，且五动词均能由 method+body 表达。

### D3. envelope 提取收口为单一解析实现，保留各调用点的 print/throw 策略
新增私有提取 `ExtractEnvelope(JsonNode? node, HttpResponseMessage response) -> Envelope`（`record(bool HasBody, bool Success, JsonNode? Data, string Error, string? Code)`，其中 `Success = node?["success"]?.GetValue<bool>() ?? response.IsSuccessStatusCode`）与 `FailureExitCode(HttpResponseMessage) -> int`（`NotFound ? 4 : 1`）。`PrintResponseAsync`/`PrintRawResponseAsync`/`ReadPostResultAsync`/`ReadSuccessDataAsync`/`PrintProjectListAsync`/`PrintSystemInfoAsync` 全部改为读这个提取结果再各自决定 print/throw；`MohistCliCommands.Agent.cs:860 ReadDataOrPrintErrorAsync` 与 `Otel.cs:181` 的外部重写改为委托。

`ReadSuccessDataAsync` **保持 throw `ApiResponseException` 契约不变**（其 7 个调用者依赖 catch）——只有“字段提取”被共享，throw/print 决策留在调用点。这精确匹配 spec“extraction … consolidated to a single parsing implementation”的措辞，把爆炸半径压到最小。

**Alternatives:**
- 把三个 reader 合并成一个参数化方法：被否——`RawResponse`（打印整根）、`PrintResponse`（打印 data）、`PostResult`（构造 record）成功路径输出形状不同，合并会放大回归面。

### D4. 彻底移除 legacy `model` 配置路径
- `ConfigService.cs:27` 删 schema `["model"]` 条目。
- `GetAgentConfigAsync`（L139–148）删整段 `// 向后兼容` fallback，并修正上方 XML doc（L116–118）与 `GetVariables` 的 XML doc（L182–183）中对 fallback 的引用。
- `SetAgentModelAsync`（L214–215）删 `await ClearAsync("model")` 及注释。
- 测试：删 `GetVariables_OnlyLegacyModelSet_SynthesizesAgentObject`；`GetAll_MasksSecrets`（L70–78）当前用 `model` 作 round-trip 探针，删 schema 后 `SetAsync("model",…)` 会抛——改用既有非敏感键（如 `logLevel`）作探针。

无需迁移/垫片。所有 API 消费者（`ConfigRoutes`/`OpencodeRoutes`/`AgentJobGrain` 等）经 `GetAgentConfigAsync`/`SetAgentModelAsync` 取值，统一走 `agent` 对象，不受影响。

## Risks / Trade-offs

- **[裸 verb 方法的网络异常从“抛出”变“优雅退出 1”]** → 用既有 CLI spec 核对；若 `PrintGetAsync` 等的网络失败路径无覆盖，补一条 spec 断言“server 不可达 → stderr 含 ServerUnavailableMessage + exit 1”。
- **[`Agent.cs:868` 潜在 bug `IsSuccessStatusCode ? null : null`]** 委托到共享提取后空 body/失败分支的返回可能改变 → 替换实现里对空 body 与失败显式保留原 null 语义，并由既有 agent spec 把关。
- **[`PrintSystemInfoAsync` 的特殊恢复路径]** 其 `HttpRequestException → RenderSystemInfoDegradedAsync + exit 0` 不可并入通用 server-unavailable → 保持该 catch 就地；通用发送方法只集中请求构造，不吞这条命令级恢复。
- **[`Otel.cs:197` 额外捕获 `TaskCanceledException`]** 是唯一处理超时处 → 其 envelope 提取可委托 D3，但 catch 范围保持不动，避免超时语义漂移。
- **[`GetAll_MasksSecrets` 探针失效]** → 同一改动里把探针键换成 `logLevel`（既有 schema 键），断言掩码行为不变。
- **[30+ 处机械改动的回归面]** → 前奏迁移以编译错误驱动一次性扫完；HTTP/envelope 部分用薄转发/委托，调用点零改动，把风险隔离在前奏那一遍 sweep。

## Migration Plan

纯重构，无数据/DB/HTTP 契约变更。按风险从低到高分四步，每步独立可验：

1. **Server legacy 移除**（隔离、最快验证）：改 `ConfigService.cs` + 重做两个测试 → `npm test`（server spec）。
2. **CLI envelope 提取收口**（D3，内部私有重构，调用点不变）：`dotnet test packages/cli/tests/Mohist.Cli.Tests`。
3. **CLI HTTP 收口**（D2，verb 方法退化为薄转发，调用点不变）：同上 CLI spec。
4. **CLI 前奏收口**（D1，~60 处调用点迁移，编译错误驱动）：删三份重复定义 → 全部改调 `api.ResolveOutputMode` / `api.ResolveProject` → CLI spec。

全量校验：`npm run build`（C# `TreatWarningsAsErrors` 兼作 lint）+ server/CLI 两套 `npm test`。

部署：`mo update server`（runner 不受影响，但可一并 `mo update runner` 保持一致）。回滚：revert commit 即可，无持久状态被改动。

**唯一运维注意点：** 现存只配置了顶层 `model`（未配 `agent` 对象）的 `config.jsonc`，部署后将不再产生 agent 配置——属本 issue 声明的 breaking 例外。运维方需在部署前后把 `model` 移入 `agent` 对象（`{ "agent": { "model": "…" } }`）。

## Open Questions

- `PrintGetSafeAsync`/`GetDataSafeAsync` 在 D2 之后是否冗余可删？（属本次范围内还是留作后续清理？）
- 共享前奏方法命名：沿用 `Resolve*` 风格（`ResolveOutputMode`/`ResolveProject`）以对齐既有 `ResolveProjectIdAsync`——确认采纳。
- D3 是否同步把 `PrintResponseAsync`/`PrintRawResponseAsync`/`ReadPostResultAsync` 合并成一个参数化 reader？本设计建议**不合**（仅共享提取，保留三 reader 以压低回归面），确认此边界。
