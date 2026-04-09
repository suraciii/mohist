## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。状态接口 SHALL 包含 LLM 配置状态。

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态
- **AND** 响应包含 `llm` 字段，反映 LLM 配置状态（见 llm-status spec）

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态
- **AND** 响应包含 `llm` 字段

## ADDED Requirements

### Requirement: API 错误响应支持 code 字段

当 API 处理 LLM 相关错误时，错误响应 SHALL 包含 `code` 字段用于错误分类。`code` 为可选字段，仅在 LLM 相关错误时出现。

#### Scenario: LLM 未配置时的错误响应
- **WHEN** Explore 或其他 LLM 依赖端点因 `LlmError` 失败
- **AND** `error.code` 为 `"LLM_NOT_CONFIGURED"`
- **THEN** 错误响应 SHALL 包含 `{ success: false, error: "<message>", code: "LLM_NOT_CONFIGURED" }`
- **AND** HTTP 状态码为 500

#### Scenario: LLM 配置格式错误时的错误响应
- **WHEN** Explore 或其他 LLM 依赖端点因 `LlmError` 失败
- **AND** `error.code` 为 `"LLM_CONFIG_INVALID"`
- **THEN** 错误响应 SHALL 包含 `{ success: false, error: "<message>", code: "LLM_CONFIG_INVALID" }`
- **AND** HTTP 状态码为 500

#### Scenario: 非 LLM 错误响应不变
- **WHEN** API 处理非 LLM 相关错误
- **THEN** 错误响应格式保持不变，不包含 `code` 字段
