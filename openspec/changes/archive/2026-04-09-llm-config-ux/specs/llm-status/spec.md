## ADDED Requirements

### Requirement: Status API 暴露 LLM 配置状态

`GET /api/status` 端点 SHALL 在响应中包含 `llm` 字段，反映当前 LLM provider 的配置状态。

当 LLM 配置有效时：
```json
{ "llm": { "configured": true, "provider": "glm", "model": "glm-4-plus" } }
```

当 LLM 未配置时：
```json
{ "llm": { "configured": false } }
```

系统 SHALL 通过 try/catch 调用 `resolveModel()` 判断配置是否有效，不抛错到外层。不暴露 apiKey 等敏感信息。

#### Scenario: LLM 已配置
- **WHEN** `~/.mohist/config.jsonc` 配置了有效的 model 和 apiKey
- **AND** 请求 `GET /api/status`
- **THEN** 响应的 `llm` 字段为 `{ configured: true, provider: "<providerID>", model: "<modelID>" }`
- **AND** `provider` 为 model 字符串中 `/` 前的部分
- **AND** `model` 为 model 字符串中 `/` 后的部分

#### Scenario: LLM 未配置
- **WHEN** `~/.mohist/config.jsonc` 不存在或未配置 model/apiKey
- **AND** 请求 `GET /api/status`
- **THEN** 响应的 `llm` 字段为 `{ configured: false }`

#### Scenario: 配置文件格式错误
- **WHEN** `~/.mohist/config.jsonc` 存在但 JSONC 语法错误
- **AND** 请求 `GET /api/status`
- **THEN** 响应的 `llm` 字段为 `{ configured: false }`
- **AND** status 端点本身 SHALL 返回 200（不因配置文件格式错误返回 5xx）

#### Scenario: 配置文件格式有效但 provider key 无效
- **WHEN** config.jsonc 中指定了 model 但对应 provider 无 apiKey
- **AND** 请求 `GET /api/status`
- **THEN** 响应的 `llm` 字段为 `{ configured: false }`
- **AND** status 端点本身 SHALL 返回 200（不因 LLM 配置问题返回 5xx）

#### Scenario: 不暴露敏感信息
- **WHEN** 请求 `GET /api/status`
- **THEN** 响应的 `llm` 字段 SHALL NOT 包含 `apiKey`、`baseURL` 或其他敏感配置
