## Context

web-ui-provider-config 功能已实现并提供了 Web UI 管理 Provider 配置的能力。在审查中发现以下问题需要改进：

1. **测试覆盖不足**: Provider API 端点缺乏专门的测试
2. **RateLimiter 内存风险**: 使用全局 Map 和定时器，生命周期不可控
3. **API Key 安全隐患**: 需要审查所有日志输出确保 API Key 不被泄露
4. **配置冲突**: CLI 和 Web UI 同时修改配置可能导致覆盖
5. **前端测试缺失**: 关键组件缺乏单元测试

当前架构：
```
Frontend (React) ←→ Backend API (Hono) ←→ Config Loader (file)
                            ↓
                    EventBus → AgentRunner (hot reload)
```

## Goals / Non-Goals

**Goals:**
- 为 Provider API 建立完整的测试覆盖（单元测试 + 集成测试）
- 重构 RateLimiter 为可管理的服务，支持生命周期控制
- 审查并加固所有日志输出，防止 API Key 泄露
- 实现配置版本控制，提供冲突检测机制
- 为关键前端组件建立测试基础设施

**Non-Goals:**
- 不修改现有的 Provider API 接口签名（保持向后兼容）
- 不引入外部存储（继续使用文件存储）
- 不实现自动配置合并（仅提供冲突检测）

## Decisions

### 1. 测试策略：分层测试

**决策**: 采用三层测试架构
- **单元测试**: 独立的函数/类测试（RateLimiter、Config Loader）
- **API 测试**: 使用 Hono 的 test client 测试路由
- **集成测试**: 测试完整的请求-响应流程

**理由**:
- 快速反馈（单元测试）+ 完整覆盖（集成测试）
- Hono 内置 test client 支持，无需额外依赖

**替代方案**: 仅使用 e2e 测试
- 缺点: 速度慢，反馈延迟

### 2. RateLimiter: 类封装 + 依赖注入

**决策**: 将 RateLimiter 封装为类，通过构造函数注入到 routes

**实现**:
```typescript
class RateLimiter {
  private map = new Map<string, RateLimitRecord>();
  private timer: NodeJS.Timeout | null = null;
  
  constructor(private windowMs: number, private maxRequests: number) {
    this.timer = setInterval(() => this.cleanup(), windowMs);
  }
  
  check(ip: string): { allowed: boolean; retryAfter?: number }
  dispose(): void // 清理定时器和 Map
}

// 在 providers.ts 中
export function createProviderRoutes(eventBus?: EventBus, rateLimiter?: RateLimiter): Hono {
  const limiter = rateLimiter ?? new RateLimiter(60000, 30);
  // ...
}
```

**理由**:
- 支持测试时的 mock/stub
- 生命周期可控（Server 关闭时 dispose）
- 避免全局状态

### 3. API Key 安全：统一的 Mask 工具

**决策**: 创建统一的敏感信息 mask 工具函数

**实现**:
```typescript
// utils/sensitive-data.ts
export function maskSensitiveData(obj: unknown): unknown {
  return JSON.parse(JSON.stringify(obj, (key, value) => {
    if (typeof key === 'string' && 
        (key.toLowerCase().includes('key') || 
         key.toLowerCase().includes('secret') ||
         key.toLowerCase().includes('token'))) {
      return typeof value === 'string' ? maskApiKey(value) : '***';
    }
    return value;
  }));
}
```

**理由**:
- 集中管理敏感字段规则
- 可复用于所有日志输出点

### 4. 配置版本控制：乐观锁

**决策**: 在配置文件中添加 `_version` 字段，保存时检查版本

**实现**:
```typescript
// 读取配置时
const config = load();
const version = config._version ?? Date.now();

// 保存配置时
function writeConfig(config: ConfigInfo, options?: { expectedVersion?: number }): void {
  if (options?.expectedVersion) {
    const current = load();
    if (current._version !== options.expectedVersion) {
      throw new ConfigConflictError('Configuration has been modified by another process');
    }
  }
  config._version = Date.now();
  // ... 写入文件
}

// API 返回 409 Conflict
```

**理由**:
- 简单有效，无需额外存储
- 向后兼容（旧配置没有 _version 字段也能工作）

**替代方案**: 文件修改时间戳
- 缺点: 精度不够，依赖文件系统

### 5. 前端测试：React Testing Library + Vitest

**决策**: 使用 React Testing Library 进行组件测试，Vitest 作为测试运行器

**实现**:
```typescript
// CustomProviderDialog.test.tsx
import { render, screen, fireEvent } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

describe('CustomProviderDialog', () => {
  it('should show validation error for invalid provider ID', () => {
    render(<CustomProviderDialog open={true} onClose={vi.fn()} />, { wrapper });
    // ... 测试逻辑
  });
});
```

**理由**:
- 项目已使用 Vitest（检查 package.json）
- React Testing Library 是 React 组件测试的标准

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| 乐观锁增加 API 复杂度 | 仅在 Web UI 使用，CLI 保持原有行为 |
| 测试增加构建时间 | 分离单元测试和集成测试，CI 中并行运行 |
| RateLimiter 重构引入回归 | 保留原有测试场景，确保行为一致 |
| 前端测试环境配置复杂 | 提供文档和示例，配置一次复用 |

## Migration Plan

**部署步骤**:
1. 合并 RateLimiter 重构（向后兼容）
2. 添加 API Key mask 工具并应用
3. 添加配置版本控制（可选字段，向后兼容）
4. 添加测试（不影响运行时）

**Rollback**:
- RateLimiter: 回退到全局 Map 实现
- 版本控制: 忽略 `_version` 字段
- 所有变更都是增量式的，可独立回滚

## Open Questions

1. 是否需要在前端显示配置版本冲突提示？
   - 建议：Phase 1 仅在后端返回 409，前端显示通用错误
   
2. RateLimiter 是否需要持久化（重启后保留计数）？
   - 建议：不需要，单机使用场景下重启后重置是可接受的

3. 前端测试覆盖率目标？
   - 建议：核心组件（Dialog、SettingsPage）达到 80%+，工具函数 100%
