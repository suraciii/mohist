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

**决策**: 使用 `try-finally` 确保 cleanup 执行，添加 connection 关闭

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

**决策**: 在 buildSystemPrompt 中根据 OpenSpec 检测结果动态注入指令

```
当检测到 OpenSpec Change 目录时，plan stage 指令:
1. 使用 spawn_coder 探索代码库，在 Change 目录下创建:
   - proposal.md: 问题描述和方案概述
   - design.md: 技术设计
   - specs/{capability}/spec.md: 按能力分解的需求规格
2. 使用 run_self_review 验证 specs 完整性（最多 3 次迭代）
3. 审查通过后使用 generate_prd 生成 prd.json
4. advance_stage 到 review（而非直接到 build）

当无 Change 目录时，使用原有 plan prompt。
```

**集成点**: `main-agent.ts` 的 `buildSystemPrompt()` 需要接收 worktreePath 以检测 Change

**依赖**: D6（工具注册）、D10（review stage）

#### D8: change-creator bug 修复

**Bug 1: isNew 标记**

```typescript
if (force) {
  fs.rmSync(existingPath, { recursive: true, force: true });
  changeName = baseName;
  isNew = true;   // 删旧建新，语义上是新建
}
```

**Bug 2: findNextVersion 冲突**

```typescript
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
  let v = maxVersion + 1;
  while (fs.existsSync(path.join(changesDir, `${baseName}-v${v}`))) {
    v++;
  }
  return `${baseName}-v${v}`;
}
```

#### D9: 统一检测逻辑

**决策**: 提取 `findChangeDir()` 共享函数，两个检测入口都调用它

```typescript
// detector.ts - 新增导出
export function findChangeDir(cwd: string, issueNumber: number): string | null {
  const changesDir = path.join(cwd, '.mohist-specs', 'changes');
  if (!fs.existsSync(changesDir)) return null;

  const entries = fs.readdirSync(changesDir, { withFileTypes: true });
  const prefix = `${issueNumber}-`;
  const matching = entries
    .filter(e => e.isDirectory() && e.name.startsWith(prefix))
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

`workflow-loader.ts` 和 `detector.ts` 都调用 `findChangeDir()`。

#### D10: Review 阶段集成（新增）

**决策**: 在默认 workflow 和转换表中加入 review stage

**当前状态**:
```typescript
// advance-stage.ts - 缺少 Review
const M1_ALLOWED_TRANSITIONS = {
  [Stage.Draft]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build],       // ← 应包含 Review
  [Stage.Build]: [Stage.Check],
  [Stage.Check]: [Stage.Done, Stage.Plan],
};

// workflow-loader.ts - 缺少 review stage
stages: [plan, build, check]         // ← 应包含 review
```

**修复后**:
```typescript
// advance-stage.ts
const M1_ALLOWED_TRANSITIONS = {
  [Stage.Draft]: [Stage.Plan],
  [Stage.Plan]: [Stage.Review, Stage.Build],  // review 可选
  [Stage.Review]: [Stage.Build],
  [Stage.Build]: [Stage.Check],
  [Stage.Check]: [Stage.Done, Stage.Plan],
};
```

```typescript
// workflow-loader.ts - 默认 workflow
stages: [
  { stage: 'plan', prompt: '...', approval: false, timeout: 600 },
  { stage: 'review', prompt: '审查 Change 产物（proposal/design/specs），确认质量', approval: true, timeout: 120 },
  { stage: 'build', prompt: '...', approval: true, timeout: 1800 },
  { stage: 'check', prompt: '...', approval: true, timeout: 600 },
]
```

**关键设计点**:
- `plan → review` 和 `plan → build` 都允许：传统 issue 直接 plan→build，OpenSpec issue plan→review→build
- Agent 通过 `read_workflow` 的 OpenSpec 检测结果决定走哪条路径
- review stage 的 `approval: true` 确保用户有机会审查 specs
- review stage 的 prompt 简短，因为审查主要靠人工

**向后兼容**: 传统 issue 走 `plan → build` 跳过 review，因为 agent 的系统提示会根据 OpenSpec 检测结果引导行为。

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
    │
    ├── D4: 多 Change 处理 (T-004)
    │   └── D9: 统一检测 (T-009, 依赖 T-004)
    │
    ├── D5: 失败分类框架 (T-005, 依赖 T-001, T-002)
    │
    ├── D6: 工具注册 (T-006)
    │   └── D7: Plan 感知 (T-007, 依赖 T-006)
    │
    ├── D8: change-creator bug (T-008, 独立)
    │
    ├── D10: Review 阶段集成 (T-011, 独立)
    │   └── D7 依赖 D10 (plan→review 转换)
    │
    ├── D11: archive 报告修复 (T-012, 独立)
    │
    ├── D12: 文档修复 (T-013, 独立)
    │
    └── T-014: 端到端验证 (依赖全部)
```
