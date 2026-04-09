## 技术设计

### 上下文

**当前代码问题位置:**
```
packages/cli/src/
├── openspec/
│   ├── ralph-executor.ts     - 资源泄漏/竞态/内存
│   ├── detector.ts           - 多 Change + 重复检测
│   ├── change-creator.ts     - isNew/findNextVersion bug
│   └── context-assembler.ts  - 不受影响
├── agents/
│   └── main-agent.ts         - 8 个工具未注册 + plan 无感知
├── workflow/
│   └── workflow-loader.ts    - 缺少 review stage + 重复检测
├── tools/
│   ├── advance-stage.ts      - Review 不在转换表中
│   ├── archive-change.ts     - rename 后读旧路径
│   ├── read-prd.ts           - 已实现未注册
│   ├── read-spec.ts          - 已实现未注册
│   ├── session-memory.ts     - 已实现未注册
│   ├── task-status.ts        - 已实现未注册
│   └── self-review.ts        - 已实现未注册
└── docs/
    ├── OPENSEPCE-USAGE.md    - 拼写错误
    ├── README.md             - 旧 workflow 描述
    └── workflow-example/
        └── workflow-openspec.yaml - schema 不匹配
```

**端到端流程断点（当前状态）:**
```
mo propose 42
    │
    ▼
┌─ Plan Stage ─────────────────────────────────────────────┐
│  Agent 收到 prompt: "分析 issue #42..."                    │
│       │                                                  │
│       ├─ 想调用 run_self_review → ❌ 工具不存在            │
│       ├─ 想调用 generate_prd    → ❌ 工具不存在            │
│       ├─ 想调用 store_learning  → ❌ 工具不存在            │
│       │                                                  │
│       └─ 只能用 spawn_coder 产出临时计划                    │
│                                                          │
│  advance_stage("build")  ← review 被完全跳过              │
└──────────────────────────────────────────────────────────┘
    │
    ▼
┌─ Build Stage ─────────────────────────────────────────────┐
│  read_workflow: mode = traditional (无 prd.json)           │
│  Agent: spawn_coder (传统模式，非 Ralph 循环)              │
└──────────────────────────────────────────────────────────┘
    │
    ▼
┌─ Check Stage ─────────────────────────────────────────────┐
│  Agent: spawn_coder("运行测试、lint...")                   │
│  archive_change 可用，但报告数据为空                        │
└──────────────────────────────────────────────────────────┘
```

**修复后目标流程:**
```
mo propose 42
    │
    ▼
┌─ Plan Stage (OpenSpec 模式) ──────────────────────────────┐
│  Agent 感知 OpenSpec 模式（通过系统提示）                   │
│       │                                                  │
│       ├─ spawn_coder: 探索代码库，创建 specs               │
│       ├─ run_self_review: 验证 specs 完整性               │
│       ├─ generate_prd: 生成 prd.json                     │
│       └─ advance_stage("review")                          │
└──────────────────────────────────────────────────────────┘
    │
    ▼ (approval gate: 用户审查)
┌─ Review Stage ────────────────────────────────────────────┐
│  approval: true → 等待用户审批                             │
│  用户满意 → 手动继续                                      │
└──────────────────────────────────────────────────────────┘
    │
    ▼
┌─ Build Stage (Ralph 循环) ────────────────────────────────┐
│  read_workflow: mode = openspec (prd.json 存在)            │
│  run_ralph_loop: 逐 task 执行                              │
│       ├─ 读取 prd.json → 按序执行 task                     │
│       ├─ 每次组装完整上下文 (proposal+design+spec+learnings)│
│       ├─ 失败分类 + 重试 + 学习记录                        │
│       └─ 全部完成 → advance_stage("check")                │
└──────────────────────────────────────────────────────────┘
    │
    ▼
┌─ Check Stage ─────────────────────────────────────────────┐
│  spawn_coder: 运行测试、lint、typecheck                     │
│  approval: true → 用户验收                                 │
│  archive_change: 归档 Change + 生成执行报告                 │
│  advance_stage("done")                                     │
└──────────────────────────────────────────────────────────┘
```

### 设计决策

#### D1: 资源清理策略

**决策**: 使用 `try-finally` 确保 cleanup 执行，**使用 Promise.allSettled 避免清理错误掩盖原始错误**

```typescript
async function executeTaskWithSpawn(...): Promise<SpawnResult> {
  const stream = ndJsonStream(input, output);
  const connection = new ClientSideConnection(handlers, stream);
  
  const cleanup = async () => {
    // 使用 allSettled 确保所有清理都尝试，不互相影响
    const results = await Promise.allSettled([
      connection.close().catch(() => {}),
      stream.cancel().catch(() => {}),
    ]);
    
    // 记录清理失败但不抛出
    results.forEach((result, index) => {
      if (result.status === 'rejected') {
        console.error(`[ralph-executor] Cleanup ${index} failed:`, result.reason);
      }
    });
    
    ensureKill();
  };
  
  try {
    // ... 执行逻辑
  } finally {
    await cleanup();
  }
}
```

**关键改进**:
1. `Promise.allSettled` 确保所有清理都执行，一个失败不影响其他
2. 清理失败只记录日志，不掩盖原始错误
3. `ensureKill` 始终在最后执行，确保进程终止

#### D2: 竞态条件修复

**决策**: 使用原子标志 + 单次清理回调，**增加timeout清理**

```typescript
let cleanupDone = false;
const doCleanup = () => {
  if (cleanupDone) return;
  cleanupDone = true;
  
  // 清理timeout避免重复触发
  if (timeoutId) clearTimeout(timeoutId);
  
  try { proc.kill('SIGTERM'); } catch {}
  
  setTimeout(() => {
    try { proc.kill('SIGKILL'); } catch {}
  }, 5000);
};

process.on('exit', doCleanup);
timeoutPromise.then(() => { doCleanup(); resolve(...); });
successCase.then(() => { doCleanup(); resolve(...); });
```

**关键修复**: `doCleanup` 中必须清除 `timeoutId`，防止timeout在清理后仍然触发。

#### D3: 内存限制策略

**决策**: 限制 agentText 最大长度为 **2MB**（从10MB下调，30分钟任务更合理）

```typescript
const MAX_AGENT_TEXT_LENGTH = 2 * 1024 * 1024; // 2MB

if (agentText.length < MAX_AGENT_TEXT_LENGTH) {
  agentText += newText;
} else if (!agentText.endsWith('...[truncated]')) {
  // 保留开头和结尾，中间截断，保留更多上下文用于诊断
  const keepLength = Math.floor(MAX_AGENT_TEXT_LENGTH / 2);
  const head = agentText.slice(0, keepLength);
  const tail = agentText.slice(-keepLength);
  agentText = `${head}\n\n...[truncated ${agentText.length - MAX_AGENT_TEXT_LENGTH} characters]...\n\n${tail}`;
}
```

**设计理由**:
- 30分钟任务按1000字符/秒计算，约180万字符
- 2MB约200万字符，刚好覆盖30分钟输出
- 保留开头和结尾比简单截断更有诊断价值

#### D4: 多 Change 处理

**决策**: detector 返回最新的 matching change，**使用精确匹配避免误匹配**

```typescript
const matchingChanges = changeDirs
  .filter(dir => {
    // 精确匹配: "42-fix" 或 "42-fix-v2"，但不匹配 "42-fix-bug"
    const exactMatch = new RegExp(`^${issuePrefix}[^-]+(-v\\d+)?$`);
    return exactMatch.test(dir);
  })
  .sort((a, b) => {
    const vA = parseInt(a.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    const vB = parseInt(b.match(/-v(\d+)$/)?.[1] ?? '1', 10);
    return vB - vA;
  });

const matchingChange = matchingChanges[0];
```

**关键修复**: 使用正则 `^${issuePrefix}[^-]+(-v\d+)?$` 精确匹配，避免 `42-fix` 匹配到 `42-fix-bug`。

#### D5: 失败分类（已存在，无需新增框架）

**现状**: `ralph-executor.ts` 已实现 `categorizeFailure` 函数和 `FAILURE_CATEGORY_CONFIGS`

```typescript
// 已存在的实现
export type FailureCategory = 'ac_not_met' | 'environment' | 'dependency' | 'timeout';

export const FAILURE_CATEGORY_CONFIGS: Record<FailureCategory, FailureCategoryConfig> = {
  ac_not_met: { maxAttempts: 3, retryable: true },
  environment: { maxAttempts: 2, retryable: true },
  dependency: { maxAttempts: 1, retryable: false },
  timeout: { maxAttempts: 1, retryable: false },
};

export function categorizeFailure(error: string): FailureCategory {
  // 基于错误关键词的分类逻辑...
}
```

**决策**: **移除 T-005 任务**。失败分类功能已在 T-001/T-002 修复的 `ralph-executor.ts` 中完整实现，无需额外的框架层。

**T-005 改为**: 验证现有分类逻辑覆盖率和正确性。

#### D6: 工具注册策略

**决策**: 在 main-agent.ts 中注册所有 OpenSpec 工具，让 Agent 自行决定何时调用

```typescript
toolRegistry.register(createReadPrdTool({ cwd: context.worktreePath }));
toolRegistry.register(createReadSpecTool({ cwd: context.worktreePath }));
toolRegistry.register(createStoreLearningTool({ cwd: context.worktreePath }));
toolRegistry.register(createLoadLearningsTool({ cwd: context.worktreePath }));
toolRegistry.register(createUpdateTaskStatusTool({ cwd: context.worktreePath }));
toolRegistry.register(createGetTaskStatusTool({ cwd: context.worktreePath }));
toolRegistry.register(createRunSelfReviewTool({ cwd: context.worktreePath }));
toolRegistry.register(createGeneratePrdTool({ cwd: context.worktreePath }));
```

#### D7: Plan Stage OpenSpec 感知

**决策**: **异步预检测 + Session 缓存**，避免同步IO和时序问题

```typescript
// runMainAgent 中预检测
export async function runMainAgent(
  context: MainAgentContext,
  sessionManager: SessionManager,
  existingSession?: Session,
): Promise<MainAgentResult> {
  // 预检测OpenSpec状态（异步，只执行一次）
  const openSpecDetection = await detectOpenSpecForIssueAsync(
    context.worktreePath, 
    context.issue.number
  );
  
  // 存入session供后续使用
  const session = existingSession ?? sessionManager.create(Number(context.issue.id));
  session.set('openSpecDetection', openSpecDetection);
  
  const system = buildSystemPrompt(context.issue, openSpecDetection);
  // ...
}

// buildSystemPrompt 从参数读取，不再同步检测
function buildSystemPrompt(issue: Issue, detection: OpenSpecDetection): string {
  const basePrompt = `...基础指令...`;
  
  if (detection.detected) {
    return basePrompt + `\n\n## OpenSpec Mode\nChange detected at ${detection.changePath}...`;
  }
  
  return basePrompt;
}
```

**关键设计**:
1. **异步预检测**: `runMainAgent` 启动时异步检测，避免同步IO阻塞
2. **Session缓存**: 检测结果存入session，避免每次prompt都重新检测
3. **状态一致性**: 检测在agent生命周期开始时完成，后续不再变化

**注入的Plan指令**:
```
当 Change 目录存在且 prd.json 不存在时（plan阶段进行中）:
1. 使用 spawn_coder 探索代码库，在 Change 目录下创建:
   - proposal.md: 问题描述和方案概述
   - design.md: 技术设计
   - specs/{capability}/spec.md: 按能力分解的需求规格
2. 使用 run_self_review 验证 specs 完整性（最多 3 次迭代）
3. 审查通过后使用 generate_prd 生成 prd.json
4. advance_stage 到 review（而非直接到 build）

当 Change 目录不存在时，使用原有 plan prompt。
```

**依赖**: D6（工具注册）、D10（review stage转换）

**集成点**: `main-agent.ts` 的 `runMainAgent()` 需要改为 async 并传入 detection 结果

#### D8: change-creator bug 修复

**Bug 1: isNew 标记**

```typescript
if (force) {
  fs.rmSync(existingPath, { recursive: true, force: true });
  changeName = baseName;
  isNew = true;   // 删旧建新，语义上是新建
}
```

**Bug 2: findNextVersion 冲突（精确匹配修复）**

```typescript
function findNextVersion(changesDir: string, baseName: string): string {
  const existing = fs.readdirSync(changesDir);
  
  // 使用精确匹配，避免 "42-fix" 匹配到 "42-fix-view"
  const versions = existing
    .filter(name => {
      // 匹配 baseName 或 baseName-v{N}
      const exactMatch = new RegExp(`^${baseName}(-v\\d+)?$`);
      return exactMatch.test(name);
    })
    .map(name => {
      const match = name.match(/-v(\d+)$/);
      return match ? parseInt(match[1], 10) : 1;
    });

  const maxVersion = Math.max(...versions, 0);
  const nextName = maxVersion === 0 ? baseName : `${baseName}-v${maxVersion + 1}`;

  if (!fs.existsSync(path.join(changesDir, nextName))) {
    return nextName;
  }
  let v = maxVersion + 1;
  while (fs.existsSync(path.join(changesDir, `${baseName}-v${v}`))) {
    v++;
  }
  return `${baseName}-v${v}`;
}
```

**关键修复**: `baseName + '-v'` 改为正则 `^${baseName}(-v\d+)?$`，避免 `42-fix` 匹配到 `42-fix-view`。

#### D9: 统一检测逻辑

**决策**: 提取 `findChangeDir()` 共享函数，**使用与 D4 相同的精确匹配**

```typescript
// detector.ts - 新增导出
export function findChangeDir(cwd: string, issueNumber: number): string | null {
  const changesDir = path.join(cwd, '.mohist-specs', 'changes');
  if (!fs.existsSync(changesDir)) return null;

  const entries = fs.readdirSync(changesDir, { withFileTypes: true });
  const prefix = `${issueNumber}-`;
  const matching = entries
    .filter(e => {
      if (!e.isDirectory()) return false;
      // 精确匹配: "42-fix" 或 "42-fix-v2"，不匹配 "42-fix-bug"
      const exactMatch = new RegExp(`^${prefix}[^-]+(-v\\d+)?$`);
      return exactMatch.test(e.name);
    })
    .map(e => e.name)
    .sort((a, b) => {
      const vA = parseInt(a.match(/-v(\d+)$/)?.[1] ?? '1', 10);
      const vB = parseInt(b.match(/-v(\d+)$/)?.[1] ?? '1', 10);
      return vB - vA;
    });

  return matching.length > 0
    ? path.join(changesDir, matching[0])
    : null;
}
```

`workflow-loader.ts` 和 `detector.ts` 都调用 `findChangeDir()`，确保行为完全一致。

#### D10: Review 阶段集成（重新设计 - 解决向后兼容）

**问题**: 直接在默认 workflow 中添加 review stage 会破坏现有项目

**新方案: 动态 Stage（推荐）**

不在默认 workflow 中添加 review stage，而是通过 **advance_stage 允许 plan→review 转换**，让 agent 动态决定路径：

```typescript
// advance-stage.ts - 允许 plan→review 和 review→build
const M1_ALLOWED_TRANSITIONS = {
  [Stage.Draft]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build, Stage.Review],  // review 可选，不是必须
  [Stage.Review]: [Stage.Build],               // review 完成后进入 build
  [Stage.Build]: [Stage.Check],
  [Stage.Check]: [Stage.Done, Stage.Plan],
};

// workflow-loader.ts - 默认 workflow **保持不变**
const DEFAULT_WORKFLOW: WorkflowConfig = {
  stages: [
    { stage: 'plan', prompt: '...', approval: false, timeout: 600 },
    // review stage **不在**默认 workflow 中
    { stage: 'build', prompt: '...', approval: true, timeout: 1800 },
    { stage: 'check', prompt: '...', approval: true, timeout: 600 },
  ],
  source: 'builtin',
};
```

**Agent 决策逻辑**（在 plan stage prompt 中说明）:
```
Plan stage 完成后，根据情况选择:
1. 如果是 OpenSpec Change（prd.json 已生成）→ advance_stage("review")
2. 如果是传统 issue（无 Change）→ advance_stage("build")
```

**向后兼容保证**:
- 现有项目 workflow 仍是 `[plan, build, check]`，无变化
- OpenSpec 项目 agent 通过 `advance_stage("review")` 进入 review
- `read_workflow` 返回的 stages 列表不包含 review，但 `advance_stage` 允许进入 review
- review stage 的行为由 advance_stage 的 targetStageConfig 处理（approval: true 等）

**关键设计点**:
- 默认 workflow **不**包含 review stage
- `advance_stage` 工具支持 `plan→review` 和 `review→build`
- 如果目标 stage 不在 workflow 配置中，使用默认配置 `{ approval: true, timeout: 600 }`
- 这允许动态添加 stage 而不修改默认 workflow

#### D11: archive_change 报告时序修复（新增）

**问题**: `renameSync` 在 L129/139 执行后，`generateReport` 在 L131/141 仍用 `change.changePath`（旧路径）读取 task-status 和 session-memories，但这些文件已移动。

**修复**: 在 `renameSync` 之前生成报告，或在 `renameSync` 之后使用新路径。

```typescript
// 方案: 先生成报告再移动
const report = generateReport(change.changePath, changeName, archivePath);

fs.renameSync(change.changePath, archivePath);

const reportPath = path.join(archivePath, 'execution-report.json');
fs.writeFileSync(reportPath, JSON.stringify(report, null, 2), 'utf-8');
```

#### D12: 文档修复（新增）

1. 重命名 `docs/OPENSEPCE-USAGE.md` → `docs/OPENSPEC-USAGE.md`
2. 更新 `docs/workflow-example/workflow-openspec.yaml`：`stages[].name` → `stages[].stage`
3. 更新 `docs/README.md`：移除旧的 7-stage 描述，替换为当前 `draft → plan → review → build → check → done`

### 测试策略

1. **单元测试**: 模拟 connection/stream 关闭验证
2. **集成测试**: 长任务执行后检查资源释放
3. **边界测试**: agentText 达到 10MB 时的行为
4. **工具注册测试**: 验证 main-agent 注册的工具数量（≥15）
5. **change-creator 测试**: isNew 语义、findNextVersion 冲突处理
6. **检测统一测试**: 两个入口返回一致结果
7. **转换表测试**: plan→review→build、plan→build 都允许
8. **archive-change 测试**: 报告在移动前生成，内容完整

### 回滚策略

变更完全向后兼容，如果发现问题：
1. 回滚到上一版本 git commit
2. 重新编译 `npm run build`
3. 无数据迁移需求

### 依赖关系

```
ralph-executor-stability-fix
    │
    ├── D1~D3: ralph-executor 稳定性修复 (T-001~T-003)
    │   └── T-001 → T-002 (竞态条件依赖资源清理)
    │
    ├── D4: 多 Change 处理 (T-004)
    │   └── D9: 统一检测 (T-009, 依赖 T-004)
    │       └── T-004 → T-009
    │
    ├── D5: 失败分类验证 (T-005, 独立，验证现有代码)
    │
    ├── D6: 工具注册 (T-006, 独立)
    │   └── D7: Plan 感知 (T-007, 依赖 D6 + D10)
    │       └── T-006 + T-011 → T-007
    │
    ├── D8: change-creator bug (T-008, 独立)
    │
    ├── D10: Review 阶段动态集成 (T-010, 独立, 重新设计)
    │   └── T-007 依赖 T-010 (plan→review 转换)
    │
    ├── D11: archive 报告修复 (T-011, 独立)
    │
    ├── D12: 文档修复 (T-012, 独立)
    │
    └── T-013: 端到端验证 (依赖全部, 工作量增加)
```

**关键路径**: T-006 → T-010 → T-007 → T-013
**最长链**: T-004 → T-009 (并行), T-001 → T-002 (串行)
