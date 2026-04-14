## ADDED Requirements

### Requirement: models.dev snapshot 生成

系统 SHALL 在构建时（`npm run build`）从 `https://models.dev/api.json` 获取完整的 provider 和模型数据，生成 TypeScript snapshot 文件 `src/config/models-snapshot.ts`。snapshot SHALL 导出一个 `Record<string, ModelsDevProvider>` 类型的常量。

#### Scenario: 正常构建生成 snapshot
- **WHEN** 执行 `npm run build`
- **THEN** 系统 SHALL fetch `https://models.dev/api.json`
- **AND** 生成 `src/config/models-snapshot.ts` 包含完整的 provider 和模型数据

#### Scenario: models.dev 不可用时构建
- **WHEN** `https://models.dev/api.json` 请求超时或失败
- **AND** 已存在 `src/config/models-snapshot.ts`
- **THEN** 构建 SHALL 复用现有 snapshot 并继续完成
- **AND** 开发者 SHALL 能通过环境变量 `MODELS_DEV_API_JSON` 指定本地 JSON 文件生成 snapshot

### Requirement: 运行时模型缓存

系统 SHALL 在运行时从本地缓存读取 models.dev 数据。缓存文件位置为 `~/.mohist/cache/models.json`，TTL 为 1 小时。

#### Scenario: 缓存命中且未过期
- **WHEN** `~/.mohist/cache/models.json` 存在且修改时间在 1 小时内
- **THEN** 系统 SHALL 直接读取缓存，不发起网络请求

#### Scenario: 缓存不存在或已过期
- **WHEN** 缓存文件不存在或超过 1 小时
- **THEN** 系统 SHALL 尝试从 `https://models.dev/api.json` 拉取最新数据
- **AND** 拉取成功后写入缓存文件
- **AND** 拉取失败时回退到构建时内嵌的 snapshot

#### Scenario: 完全离线环境
- **WHEN** 缓存不存在或已过期
- **AND** 网络 fetch 失败
- **THEN** 系统 SHALL 使用构建时内嵌的 snapshot 数据
- **AND** 系统 SHALL 正常启动，不报错

### Requirement: 后台定期刷新

系统 SHALL 每 60 分钟后台刷新一次 models.dev 缓存。刷新失败 SHALL 静默跳过，不影响正在运行的系统。

#### Scenario: 定期刷新成功
- **WHEN** 后台刷新定时器触发
- **AND** fetch 成功
- **THEN** 缓存文件 SHALL 被更新
- **AND** 内存中的模型数据 SHALL 被刷新

#### Scenario: 定期刷新失败
- **WHEN** 后台刷新定时器触发
- **AND** fetch 失败
- **THEN** 系统 SHALL 继续使用当前缓存/snapshot
- **AND** 不 SHALL 抛出错误或影响服务

### Requirement: provider SDK 类型映射

系统 SHALL 从 models.dev 数据的 `npm` 字段映射到 mohist 内部 SDK 类型：

| models.dev npm | mohist sdk |
|---|---|
| `@ai-sdk/anthropic` | `anthropic` |
| `@ai-sdk/openai` | `openai` |
| 其他所有值 | `openai-compatible` |

#### Scenario: 映射 anthropic SDK
- **WHEN** models.dev 中 provider 的 `npm` 字段为 `@ai-sdk/anthropic`
- **THEN** 系统 SHALL 将该 provider 的 sdk 映射为 `"anthropic"`

#### Scenario: 映射 openai SDK
- **WHEN** models.dev 中 provider 的 `npm` 字段为 `@ai-sdk/openai`
- **THEN** 系统 SHALL 将该 provider 的 sdk 映射为 `"openai"`

#### Scenario: 映射 openai-compatible SDK
- **WHEN** models.dev 中 provider 的 `npm` 字段为 `@ai-sdk/openai-compatible` 或任何其他值
- **THEN** 系统 SHALL 将该 provider 的 sdk 映射为 `"openai-compatible"`

### Requirement: 模型列表 API 返回全限定 ID

`GET /providers/models` API 返回的每个 model 的 `id` 字段 SHALL 使用全限定格式 `provider/model-id`（如 `"minimax/MiniMax-M2.7"`），而非裸 model ID。

#### Scenario: models.dev provider 的模型
- **WHEN** provider "minimax" 在 models.dev 中有模型 `MiniMax-M2.7`
- **THEN** API 返回的 model id SHALL 为 `"minimax/MiniMax-M2.7"`

#### Scenario: custom provider 的模型
- **WHEN** 用户在 config.jsonc 中定义了 `provider.custom-llm.models = ["my-model-v1"]`
- **THEN** API 返回的 model id SHALL 为 `"custom-llm/my-model-v1"`
