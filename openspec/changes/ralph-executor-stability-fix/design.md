## 技术设计

### 上下文

**当前代码问题位置:**
```
packages/cli/src/openspec/
├── ralph-executor.ts (426行) - 主要问题所在
│   ├── L142-144: stream 和 connection 未关闭
│   ├── L127: agentText 无界增长
│   └── L131-140: ensureKill 竞态条件
├── detector.ts (50行) - 需增强
│   └── 只返回第一个 matching change
└── context-assembler.ts (236行) - 不受影响
```

**问题分析:**

```
每次 Task 执行的资源生命周期:
┌─────────┐    ┌─────────┐    ┌─────────┐    ┌─────────┐
│ spawn   │───▶│ stream  │───▶│ session │───▶│ cleanup │
│process  │    │ create  │    │ execute │    │ ???     │
└─────────┘    └────┬────┘    └────┬────┘    └────┬────┘
                    │              │              │
                    ▼              ▼              ▼
                 创建但不关闭   可能泄漏     不完整!
```

### 设计决策

#### D1: 资源清理策略

**决策**: 使用 `try-finally` 确保 cleanup 执行，添加 connection 关闭

**实现**:
```typescript
// ralph-executor.ts
async function executeTaskWithSpawn(...): Promise<SpawnResult> {
  const stream = ndJsonStream(input, output);
  const connection = new ClientSideConnection(handlers, stream);
  
  const cleanup = async () => {
    try { await connection.close(); } catch { /* ignore */ }
    try { await stream.cancel(); } catch { /* ignore */ }
    ensureKill();
  };
  
  try {
    // ... 执行逻辑
  } finally {
    await cleanup();
  }
}
```

**理由**:
- 无论成功或失败，资源都得到清理
- 添加显式的 connection.close() 和 stream.cancel()
- finally 块确保即使抛出异常也执行清理

**替代方案**: 依赖进程退出自动清理
- 放弃原因：生产环境不可接受，句柄泄漏会导致系统资源耗尽

#### D2: 竞态条件修复

**决策**: 使用原子标志 + 单次清理回调

**当前问题**:
```typescript
// 问题代码
const ensureKill = () => {
  if (!procExited) {      // 检查 1
    procExited = true;    // 设置
    proc.kill('SIGTERM'); // 可能失败，然后重试
  }
};
// 多个调用点可能并发执行
```

**修复方案**:
```typescript
let cleanupDone = false;
const doCleanup = () => {
  if (cleanupDone) return;
  cleanupDone = true;
  
  // 先尝试 graceful shutdown
  try { proc.kill('SIGTERM'); } catch {}
  
  // 延迟强制 kill
  setTimeout(() => {
    try { proc.kill('SIGKILL'); } catch {}
  }, 5000);
};

// 所有出口点调用同一个函数
process.on('exit', doCleanup);
timeoutPromise.then(() => { doCleanup(); resolve(...); });
successCase.then(() => { doCleanup(); resolve(...); });
```

**理由**:
- 原子标志防止重复执行
- 统一 cleanup 函数确保行为一致

#### D3: 内存限制策略

**决策**: 限制 agentText 最大长度为 10MB

**实现**:
```typescript
const MAX_AGENT_TEXT_LENGTH = 10 * 1024 * 1024; // 10MB

// 在 sessionUpdate 中
if (agentText.length < MAX_AGENT_TEXT_LENGTH) {
  agentText += newText;
} else if (!agentText.endsWith('...[truncated]')) {
  agentText += '\n...[truncated]';
}
```

**理由**:
- 10MB 足够容纳大多数任务的完整输出
- 超限后添加标记而非静默截断，便于调试
- 防止内存无限增长导致 OOM

#### D4: 多 Change 处理

**决策**: detector 返回最新的 matching change

**当前行为**:
```typescript
// detector.ts - 只返回第一个
const matchingChange = changeDirs.find(dir => dir.startsWith(issuePrefix));
```

**修复方案**:
```typescript
const matchingChanges = changeDirs
  .filter(dir => dir.startsWith(issuePrefix))
  .sort((a, b) => {
    // 解析版本号: 42-feature, 42-feature-v2, 42-feature-v3
    const vA = parseInt(a.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    const vB = parseInt(b.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    return vB - vA; // 降序，最新版本在前
  });

const matchingChange = matchingChanges[0]; // 取最新
```

**理由**:
- 用户可能多次执行 `mo propose`，产生多个版本
- 应该使用最新的版本，而非第一个创建的
- 符合用户的直觉预期

#### D5: 失败分类框架

**决策**: 添加 FailureClassifier 类，预留分类接口

**设计**:
```typescript
enum FailureType {
  ACCEPTANCE_CRITERIA_NOT_MET = 'ac_not_met',
  ENVIRONMENT_ERROR = 'environment',
  CODE_DEPENDENCY = 'dependency',
  TIMEOUT = 'timeout',
  UNKNOWN = 'unknown',
}

interface FailureClassifier {
  classify(error: string, output: string): FailureType;
  shouldRetry(type: FailureType, attempts: number): boolean;
  getMaxRetries(type: FailureType): number;
}
```

**阶段划分**:
- **本变更**: 实现基础框架，所有失败默认 UNKNOWN 类型
- **T-006**: 实现具体的分类逻辑和重试策略

**理由**:
- 不阻塞 T-005-D 的集成
- 为 T-006 提供清晰的接口
- 保持向后兼容（当前行为不变）

### 测试策略

1. **单元测试**: 模拟 connection/stream 关闭验证
2. **集成测试**: 长任务执行后检查资源释放
3. **边界测试**: agentText 达到 10MB 时的行为

### 回滚策略

变更完全向后兼容，如果发现问题：
1. 回滚到上一版本 git commit
2. 重新编译 `npm run build`
3. 无数据迁移需求

### 依赖关系

```
ralph-executor-stability-fix
           │
           ├── 依赖: context-assembler.ts (已稳定)
           ├── 依赖: detector.ts (部分修改)
           │
           ▼ (阻塞)
mohist-openspec-workflow/T-005-D
           │
           ▼ (阻塞)
mohist-openspec-workflow/T-006
```