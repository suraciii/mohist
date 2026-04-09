## Context

Mohist 当前通过 CLI 命令 `mo providers login/logout` 管理 LLM Provider 配置，配置存储在 `~/.mohist/config.jsonc` 中。Web UI 使用 React + TanStack Query，Server 使用 Hono + SQLite。

参考 OpenCode 的实现，Provider 配置架构主要有两种模式：
1. **App 模式**（桌面端）：Provider + Auth 双轨存储，支持热重载
2. **Console 模式**（云端）：简化表单，数据库存储

Mohist 更适合 App 模式，因为：
- 是本地 CLI 工具，配置应保存在本地文件
- 需要与现有 CLI 配置兼容
- 不需要多用户/工作空间隔离

## Goals / Non-Goals

**Goals:**
- Web UI 提供完整的 Provider 管理功能（查看、添加、删除、自定义 Provider）
- 配置变更后热重载，无需重启 Server
- 支持连接测试，验证 Provider 可用性
- 与 CLI 配置格式 100% 兼容

**Non-Goals:**
- 不引入独立 Auth 存储（保持现有 config.jsonc 结构）
- 不支持 OAuth Provider（Phase 1 仅 API Key）
- 不实现 Provider 自动发现（手动配置模型列表）

## Decisions

### 1. 热重载机制：Event Bus 模式

**决策**：使用 Node.js EventEmitter 实现 ConfigService 和 AgentRunnerService 间的解耦通信。

**理由**：
- 最简单有效，无需引入复杂的状态管理
- 参考 OpenCode 的 `global-sync.tsx` 事件广播机制
- AgentRunner 已在运行，需要异步通知其重新初始化

**实现**：
```typescript
// ConfigService 触发变更事件
this.eventBus.emit('config:providers:changed', { providers });

// AgentRunnerService 监听并重载
this.eventBus.on('config:providers:changed', () => {
  this.llmProvider = createLLMProvider(newConfig);
});
```

**替代方案**：轮询检查配置文件修改时间
- 缺点：延迟不可控，需要额外文件系统监听

### 2. 连接测试：简单 HTTP 探活

**决策**：发送一个廉价的 LLM 请求（如 max_tokens=1）验证连通性。

**理由**：
- 能同时验证网络、认证、模型可用性
- DeepSeek/GLM 等国产模型兼容性较好
- OpenCode 没有显式测试按钮，依赖隐式加载验证

**实现**：
```typescript
await llm.generate({
  model: config.model,
  messages: [{ role: 'user', content: 'hi' }],
  max_tokens: 1
});
```

**替代方案**：发送 HEAD 请求或调用 /models 端点
- 缺点：不是所有 Provider 都支持，且无法验证 API Key 权限

### 3. 自定义 Provider 表单设计

**决策**：复用 OpenCode 的表单结构，但简化模型配置。

**字段**：
- Provider ID（唯一标识，如 "my-deepseek"）
- 显示名称
- Base URL
- API Key（密码输入框）
- 模型列表（简单数组输入，逗号分隔）

**理由**：
- OpenCode 的完整表单包含 headers、payload 等高级选项
- Mohist 场景更简单，基础字段足够

### 4. 错误处理策略

**决策**：使用 Toast 通知 + 表单内联错误显示。

**理由**：
- Web UI 已集成 Toast 系统
- 连接测试失败需要具体错误信息（如 401、timeout）

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| 配置热重载时正在运行的 Agent 可能使用旧配置 | Agent 下次运行时自动使用新配置；紧急场景提示用户重启 Server |
| API Key 在内存中可能泄露 | API Key 仅存在于服务端内存，不传输到前端；日志中 mask 处理 |
| 连接测试可能被滥用（频繁测试） | 前端防抖（2秒），服务端限流（每 IP 每分钟 10 次） |
| 自定义 Provider 配置错误导致系统不稳定 | 表单验证 provider ID 格式、baseURL 格式；保存前强制测试连接 |

## Migration Plan

**部署步骤**：
1. 合并代码后重启 Server（加载新 API 端点）
2. Web UI 自动更新（Vite HMR）
3. 无需数据迁移，复用现有 `~/.mohist/config.jsonc`

**Rollback**：
- 回滚代码后重启 Server
- 配置文件保持不变，旧版本 CLI 仍可正常读取

## Open Questions

1. 是否需要支持 Provider 启用/禁用切换（而不删除配置）？
   - 建议：Phase 1 不需要，删除后重新配置即可

2. 多模型 Provider 如何指定默认模型？
   - 建议：自定义 Provider 的第一个模型作为默认

3. 配置冲突处理（CLI 和 Web UI 同时修改）？
   - 建议：文件覆盖模式，后保存者生效（和当前行为一致）
