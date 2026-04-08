## 技术设计

### 上下文

**当前代码问题位置:**
```
packages/cli/src/
├── openspec/
│   ├── ralph-executor.ts     - 资源泄漏/竞态/内存
│   ├── detector.ts           - 多 Change + 重复检测
│   └── change-creator.ts     - isNew/findNextVersion bug
├── agents/
│   └── main-agent.ts         - 8 个工具未注册
├── workflow/
│   └── workflow-loader.ts    - 重复检测逻辑
└── tools/
    ├── read-prd.ts           - 已实现未注册
    ├── read-spec.ts          - 已实现未注册
    ├── session-memory.ts     - 已实现未注册
    ├── task-status.ts        - 已实现未注册
    └── self-review.ts        - 已实现未注册
```

**端到端流程断点:**
```
mo propose 42
    │
    ▼
┌──────────────────────────────────────────────────────────┐
│  Plan Stage                                              │
│                                                          │
│  Agent 收到 prompt: "分析 issue #42..."                   │
│       │                                                  │
│       ├─ Agent 想调用 run_self_review → ❌ 工具不存在      │
│       ├─ Agent 想调用 generate_prd    → ❌ 工具不存在      │
│       ├─ Agent 想调用 store_learning  → ❌ 工具不存在      │
│       │                                                  │
│       └─ 只能用 spawn_coder 产出临时计划 → 断开！          │
│                                                          │
│  期望: 创建 specs → self-review → generate prd.json      │
│  实际: 无 OpenSpec 感知，产出临时文本计划                   │
└──────────────────────────────────────────────────────────┘
    │
    ▼ (advance_stage → build)
┌──────────────────────────────────────────────────────────┐
│  Build Stage                                             │
│                                                          │
│  read_workflow 检测到 OpenSpec 模式                       │
│  Agent 调用 run_ralph_loop                               │
│       │                                                  │
│       ├─ detectOpenSpecChange → 找不到 prd.json          │
│       │   (因为 plan 阶段从未生成)                        │
│       │                                                  │
│       └─ 返回 "No OpenSpec Change found" → 降级传统模式   │
└──────────────────────────────────────────────────────────┘
```

### 设计决策

#### D1: 资源清理策略

**决策**: 使用 `try-finally` 确保 cleanup 执行，添加 connection 关闭

**实现**:
```typescript
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

#### D2: 竞态条件修复

**决策**: 使用原子标志 + 单次清理回调

```typescript
let cleanupDone = false;
const doCleanup = () => {
  if (cleanupDone) return;
  cleanupDone = true;
  
  try { proc.kill('SIGTERM'); } catch {}
  
  setTimeout(() => {
    try { proc.kill('SIGKILL'); } catch {}
  }, 5000);
};

process.on('exit', doCleanup);
timeoutPromise.then(() => { doCleanup(); resolve(...); });
successCase.then(() => { doCleanup(); resolve(...); });
```

#### D3: 内存限制策略

**决策**: 限制 agentText 最大长度为 10MB

```typescript
const MAX_AGENT_TEXT_LENGTH = 10 * 1024 * 1024;

if (agentText.length < MAX_AGENT_TEXT_LENGTH) {
  agentText += newText;
} else if (!agentText.endsWith('...[truncated]')) {
  agentText += '\n...[truncated]';
}
```

#### D4: 多 Change 处理

**决策**: detector 返回最新的 matching change

```typescript
const matchingChanges = changeDirs
  .filter(dir => dir.startsWith(issuePrefix))
  .sort((a, b) => {
    const vA = parseInt(a.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    const vB = parseInt(b.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    return vB - vA;
  });

const matchingChange = matchingChanges[0];
```

#### D5: 失败分类框架

**决策**: 添加 FailureClassifier 类，预留分类接口

```typescript
enum FailureType {
  ACCEPTANCE_CRITERIA_NOT_MET = 'ac_not_met',
  ENVIRONMENT_ERROR = 'environment',
  CODE_DEPENDENCY = 'dependency',
  TIMEOUT = 'timeout',
  UNKNOWN = 'unknown',
}
```

#### D6: 工具注册策略（新增）

**决策**: 在 main-agent.ts 中注册所有 OpenSpec 工具，按 stage 按需使用

**当前状态**: 只有 `run_ralph_loop` 被注册

**方案**: 注册全部 8 个工具，让 Agent 自行决定何时调用

```typescript
// main-agent.ts - 新增注册
toolRegistry.register(createReadPrdTool({ cwd: context.worktreePath }));
toolRegistry.register(createReadSpecTool({ cwd: context.worktreePath }));
toolRegistry.register(createStoreLearningTool({ cwd: context.worktreePath }));
toolRegistry.register(createLoadLearningsTool({ cwd: context.worktreePath }));
toolRegistry.register(createUpdateTaskStatusTool({ cwd: context.worktreePath }));
toolRegistry.register(createGetTaskStatusTool({ cwd: context.worktreePath }));
toolRegistry.register(createRunSelfReviewTool({ cwd: context.worktreePath }));
toolRegistry.register(createGeneratePrdTool({ cwd: context.worktreePath }));
```

**工具参数**: 所有工具需要 `cwd` 参数以定位 `.mohist-specs/` 目录

**理由**:
- 工具已实现且有测试，只需注册
- Agent 通过系统提示引导按 stage 使用
- 不需要额外的 stage 判断逻辑——工具本身是通用的

**替代方案**: 只在特定 stage 注册特定工具
- 放弃原因：需要动态修改 ToolRegistry，增加复杂度；Agent 已通过 stage 信息知道当前阶段

#### D7: Plan Stage OpenSpec 感知（新增）

**决策**: 在 buildSystemPrompt 中根据 workflow 检测结果动态注入 OpenSpec 指令

**实现**:

```
当 workflow.openspec.mode === 'openspec' 时，plan stage 指令:
1. 使用 read_workflow 检查当前 workflow 和 OpenSpec 状态
2. 探索代码库，理解需求
3. 在 .mohist-specs/changes/{change-name}/ 下创建:
   - proposal.md: 问题描述和方案概述
   - design.md: 技术设计
   - specs/{capability}/spec.md: 按能力分解的需求规格
4. 使用 run_self_review 验证 specs 完整性
5. 审查通过后使用 generate_prd 生成 prd.json
6. advance_stage 到 build

当 workflow.openspec.mode === 'traditional' 时，使用原有 plan prompt。
```

**集成点**: `main-agent.ts` 的 `buildSystemPrompt()` 函数需要接收 workflow 配置

**依赖**: 需要 D6（工具注册）先完成

#### D8: change-creator bug 修复（新增）

**Bug 1: isNew 标记**

```typescript
// 修复前
if (force) {
  fs.rmSync(existingPath, { recursive: true, force: true });
  changeName = baseName;
  isNew = false;  // ← 错误
}

// 修复后
if (force) {
  fs.rmSync(existingPath, { recursive: true, force: true });
  changeName = baseName;
  isNew = true;   // ← 删旧建新，语义上是新建
}
```

**Bug 2: findNextVersion 冲突**

```typescript
// 修复前：从 baseName 开始找，忽略已存在的版本化目录
function findNextVersion(changesDir: string, baseName: string): string {
  if (!fs.existsSync(path.join(changesDir, baseName))) {
    return baseName;  // ← 可能与已有 v2 冲突
  }
  // ...
}

// 修复后：收集所有版本号，找到最大值 +1
function findNextVersion(changesDir: string, baseName: string): string {
  const existing = fs.readdirSync(changesDir);
  const versions = existing
    .filter(name => name === baseName || name.startsWith(baseName + '-v'))
    .map(name => {
      const match = name.match(/-v(\d+)$/);
      return match ? parseInt(match[1], 10) : 1;
    });

  const maxVersion = Math.max(...versions, 0);
  const nextName = maxVersion === 0 ? baseName : `${baseName}-v${maxVersion + 1}`;

  if (!fs.existsSync(path.join(changesDir, nextName))) {
    return nextName;
  }
  // 安全回退：递增直到找到可用名
  let v = maxVersion + 1;
  while (fs.existsSync(path.join(changesDir, `${baseName}-v${v}`))) {
    v++;
  }
  return `${baseName}-v${v}`;
}
```

#### D9: 统一检测逻辑（新增）

**决策**: detector.ts 作为唯一检测入口，workflow-loader 调用它

**当前状态**:
```
workflow-loader.ts::detectOpenSpecForIssue()  → 三态: traditional / change-exists / openspec
detector.ts::detectOpenSpecChange()           → 二态: null / OpenSpecChange (仅当 prd.json 存在)
```

**方案**: 保留 detector.ts 的 `detectOpenSpecChange()` 作为底层实现，workflow-loader 的 `detectOpenSpecForIssue()` 调用它并增加"Change 目录存在但无 prd.json"的中间态

```typescript
// workflow-loader.ts
export function detectOpenSpecForIssue(cwd: string, issueNumber: number): OpenSpecDetection {
  const changeDir = findChangeDir(cwd, issueNumber);  // 复用 detector 的目录查找
  
  if (!changeDir) {
    return { detected: false, mode: 'traditional' };
  }

  const prdPath = path.join(changeDir, 'prd.json');
  
  if (!fs.existsSync(prdPath)) {
    return { detected: true, changePath: changeDir, mode: 'traditional' };
  }

  return { detected: true, changePath: changeDir, prdPath, mode: 'openspec' };
}
```

**同时**: 将 detector.ts 的目录查找逻辑提取为共享函数 `findChangeDir()`

### 测试策略

1. **单元测试**: 模拟 connection/stream 关闭验证
2. **集成测试**: 长任务执行后检查资源释放
3. **边界测试**: agentText 达到 10MB 时的行为
4. **工具注册测试**: 验证 main-agent 注册的工具数量和列表
5. **change-creator 测试**: isNew 语义、findNextVersion 冲突处理
6. **检测统一测试**: 两个入口返回一致结果

### 回滚策略

变更完全向后兼容，如果发现问题：
1. 回滚到上一版本 git commit
2. 重新编译 `npm run build`
3. 无数据迁移需求

### 依赖关系

```
ralph-executor-stability-fix
    │
    ├── D1~D5: ralph-executor 稳定性修复
    │   └── 依赖: context-assembler.ts (已稳定)
    │
    ├── D6: 工具注册
    │   └── 依赖: tools/* (已实现)
    │
    ├── D7: Plan Stage 感知
    │   └── 依赖: D6 (工具注册)
    │
    ├── D8: change-creator bug 修复
    │   └── 独立，无依赖
    │
    └── D9: 统一检测逻辑
        └── 依赖: D4 (多 Change 处理)
```
