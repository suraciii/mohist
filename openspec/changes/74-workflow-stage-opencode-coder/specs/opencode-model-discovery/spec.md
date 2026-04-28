## ADDED Requirements

### Requirement: opencode 模型发现

系统 SHALL 提供模型发现能力，通过 ACP `newSession` 响应中的 `availableModels` 字段获取当前 opencode 环境所有可用模型，并以 `{provider}/{model}` 格式返回列表。

#### Scenario: 发现可用模型列表
- **WHEN** 系统需要获取 opencode 可用模型
- **THEN** 启动短生命周期 `opencode acp` 子进程，通过 stdio JSON-RPC 发送 `initialize` → `newSession({ cwd })`
- **AND** 从 `newSession` 响应中提取 `models.availableModels` 列表
- **AND** kill 子进程并返回模型列表

#### Scenario: 模型列表缓存
- **WHEN** 模型发现完成后
- **THEN** 结果缓存于内存，TTL 为 5 分钟
- **AND** 缓存期内再次调用直接返回缓存结果，不启动新进程

#### Scenario: 强制刷新缓存
- **WHEN** 调用方请求强制刷新（bypass cache）
- **THEN** 立即启动新探测进程，忽略缓存，返回最新模型列表

### Requirement: 模型发现 API

系统 SHALL 提供 REST API 端点 `GET /api/opencode/models` 暴露模型发现结果。

#### Scenario: 获取可用模型
- **WHEN** 客户端请求 `GET /api/opencode/models`
- **THEN** 返回 JSON `{ "models": ["provider/model", ...] }`
- **AND** HTTP 200

#### Scenario: 缓存命中返回
- **WHEN** `GET /api/opencode/models` 请求在缓存 TTL 内
- **THEN** 直接返回缓存的模型列表，不启动新进程

#### Scenario: 模型发现失败
- **WHEN** ACP 探测进程启动或通信失败
- **THEN** 返回 HTTP 503 Service Unavailable
- **AND** body 包含 `{ "error": "model discovery failed", "details": "..." }`
