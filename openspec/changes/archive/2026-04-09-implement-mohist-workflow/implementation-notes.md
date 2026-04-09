# Implementation Notes

## Overview

本文档记录 `implement-mohist-workflow` 实现过程中的关键问题和解决方案。基于代码审查发现的问题进行整理。

## Critical Issues Found

### Issue 1: Planner Agent 文件写入能力缺失

**Severity**: 🔴 Critical

**问题描述**: 
当前 `planner-agent.ts` 的 `generateArtifacts` 方法调用 `streamText` 让模型生成设计文档，但没有提供文件写入工具。代码随后尝试读取文件，但这些文件实际上并未被创建。

```typescript
// 问题代码 (planner-agent.ts:310-317)
await streamText({
  model,
  system: 'You are a Planner Agent. Create high-quality design artifacts.',
  messages: [{ role: 'user', content: prompt_text }],
  tools: toolRegistry.toToolSet(), // 只有 read/grep/glob，没有 write
});

return this.readGeneratedArtifacts(changeDir); // 读取空文件！
```

**解决方案**:

选项 A: 添加 write_file 工具
- 优点：Agent 完全自主
- 缺点：Agent 可能写入错误位置或格式

选项 B: 返回结构化内容，代码写入
- 优点：更可控，可以验证内容
- 缺点：需要解析模型输出

**推荐**: 选项 B

```typescript
// 改进后的流程
const result = await streamText({...});
const content = await result.text;

// 解析模型返回的 JSON
const artifacts = this.parseArtifactContent(content);

// 验证完整性
if (!this.validateArtifacts(artifacts)) {
  throw new Error('Invalid artifacts');
}

// 代码写入文件（可控）
this.writeArtifactsToFiles(artifacts, changeDir);
```

**相关任务**: T-004-1, T-008-1

---

### Issue 2: Build 阶段缺少 Task 状态追踪

**Severity**: 🟡 Medium

**问题描述**:
`executeBuildStage` 执行 task 后没有更新 prd.json 中的 task 状态。用户无法知道哪些 task 已完成，哪些是失败的。

**解决方案**:

1. 在 `ChangeArtifactsManager` 中添加 `updateTaskStatus` 方法：

```typescript
interface TaskStatus {
  status: 'pending' | 'in_progress' | 'completed' | 'failed';
  startedAt?: string;
  completedAt?: string;
  attempts: number;
  error?: string;
}

class ChangeArtifactsManager {
  updateTaskStatus(issueNumber: number, taskId: string, status: TaskStatus): void {
    const prd = this.readPrd(issueNumber);
    if (!prd) return;
    
    const task = prd.tasks.find(t => t.id === taskId);
    if (task) {
      (task as any).status = status;
      this.writePrd(issueNumber, prd);
    }
  }
}
```

2. 在 Build 阶段更新状态：

```typescript
private async executeBuildStage(issue: Issue): Promise<StageResult> {
  for (const task of tasks) {
    // 更新为 in_progress
    this.artifactManager.updateTaskStatus(issue.number, task.id, {
      status: 'in_progress',
      startedAt: new Date().toISOString(),
      attempts: attempt
    });
    
    // 执行任务...
    
    // 更新为 completed 或 failed
    this.artifactManager.updateTaskStatus(issue.number, task.id, {
      status: taskSuccess ? 'completed' : 'failed',
      completedAt: new Date().toISOString(),
      attempts: attempt,
      error: taskSuccess ? undefined : lastError
    });
  }
}
```

**相关任务**: T-005-1

---

### Issue 3: Reviewer Agent 只运行 `npm run build`

**Severity**: 🟡 Medium

**问题描述**:
当前 `ReviewerAgent.runTests()` 只执行 `npm run build`，应该优先尝试运行实际的测试。

**解决方案**:

```typescript
private async runTests(worktreePath: string): Promise<TestResult> {
  // 1. 读取 package.json
  const packageJsonPath = path.join(worktreePath, 'package.json');
  if (!fs.existsSync(packageJsonPath)) {
    return { passed: true, skipped: true, reason: 'No package.json found' };
  }
  
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf-8'));
  const scripts = packageJson.scripts || {};
  
  // 2. 优先尝试 test
  if (scripts.test && scripts.test !== 'echo "Error: no test specified"') {
    try {
      const output = execSync('npm test 2>&1', {
        cwd: worktreePath,
        encoding: 'utf-8',
        timeout: 300000, // 5分钟
      });
      return { passed: true, output };
    } catch (error) {
      return {
        passed: false,
        issues: [{
          location: 'test',
          message: 'Tests failed',
          suggestion: (error as any).stdout?.slice(0, 1000) || 'Check test output'
        }]
      };
    }
  }
  
  // 3. 备选：build
  if (scripts.build) {
    // 执行 build...
  }
  
  // 4. 都没有则跳过
  return { passed: true, skipped: true, reason: 'No test or build script found' };
}
```

**相关任务**: T-006-1

---

### Issue 4: Prompt 模板嵌入代码

**Severity**: 🟢 Low

**问题描述**:
Planner 和 Reviewer 的默认 Prompt 嵌入在 TypeScript 代码中（`PLANNER_DEFAULT_PROMPT`、`REVIEWER_DEFAULT_PROMPT`），不利于用户自定义。

**解决方案**:

1. 创建 prompts 目录：

```
src/agents/prompts/
├── planner-default.yaml
├── planner-self-review.yaml
└── reviewer-default.yaml
```

2. 加载逻辑：

```typescript
class PromptLoader {
  private promptsDir: string;
  
  constructor(projectPath: string) {
    this.promptsDir = path.join(projectPath, 'src/agents/prompts');
  }
  
  loadPrompt(name: string): string {
    const filePath = path.join(this.promptsDir, `${name}.yaml`);
    if (fs.existsSync(filePath)) {
      return fs.readFileSync(filePath, 'utf-8');
    }
    // 返回内置默认
    return this.getBuiltinPrompt(name);
  }
  
  private getBuiltinPrompt(name: string): string {
    // 内置默认作为 fallback
    const builtins: Record<string, string> = {
      'planner-default': `...`,
      'reviewer-default': `...`,
    };
    return builtins[name] || '';
  }
}
```

3. 支持用户自定义：

```typescript
// 用户可以在 .mohist/config.yaml 中指定自定义 prompt
planner:
  prompt: ./custom-planner-prompt.yaml
```

**相关任务**: T-004-2, T-006-2

---

### Issue 5: 缺少真正的用户审批流程

**Severity**: 🔴 Critical

**问题描述**:
当前的 Plan 和 Review 阶段返回 `requiresApproval: true`，但没有实际的暂停和用户交互逻辑。Main Agent 需要处理这个状态并调用 `ask_user`。

**解决方案**:

在 Main Agent 中：

```typescript
async function executeWorkflowStage(issue: Issue, stage: Stage) {
  const result = await workflowController.executeStage(issue, stage);
  
  if (result.requiresApproval) {
    // 构建审批提示
    const approvalPrompt = this.buildApprovalPrompt(stage, result.output);
    
    // 调用 ask_user
    const decision = await askUser({
      question: approvalPrompt,
      options: [
        { label: 'Approve', value: 'approve' },
        { label: 'Request Changes', value: 'changes' },
        { label: 'Abort', value: 'abort' }
      ]
    });
    
    // 处理决策
    switch (decision) {
      case 'approve':
        await advanceStage(issue.id, getNextStage(stage));
        break;
      case 'changes':
        // 要求重新执行当前阶段
        await addComment(issue.id, 'User requested changes');
        break;
      case 'abort':
        await updateIssueStatus(issue.id, 'aborted');
        break;
    }
  }
}

private buildApprovalPrompt(stage: Stage, output: any): string {
  if (stage === Stage.Plan) {
    return `Please review the design plan for issue #${output.issueNumber}:

**Proposal Summary:**
${output.artifacts.proposal.slice(0, 500)}...

**Design Overview:**
${output.artifacts.design.slice(0, 500)}...

**Self-Review Notes:**
${output.selfReviewNotes}

Do you approve this design?`;
  }
  
  if (stage === Stage.Review) {
    return `Please review the code changes:

**Review Result:** ${output.passed ? 'PASSED' : 'FAILED'}

**Dimensions:**
${output.dimensions.map((d: any) => `- ${d.name}: ${d.passed ? '✓' : '✗'} ${d.reasoning}`).join('\n')}

${output.fixSuggestions ? `**Suggested Fixes:**\n${output.fixSuggestions.join('\n')}` : ''}

Do you approve these changes?`;
  }
  
  return 'Please review and approve.';
}
```

**相关任务**: T-007

---

### Issue 6: Main Agent 未集成 WorkflowController

**Severity**: 🔴 Critical

**问题描述**:
`main-agent.ts` 仍然使用旧的工作流逻辑，没有调用新实现的 `WorkflowController`。

**解决方案**:

1. 重构 Main Agent 初始化：

```typescript
export interface MainAgentContext {
  // ... existing fields ...
  workflowController?: WorkflowController;
}

export async function runMainAgent(
  context: MainAgentContext,
  sessionManager: SessionManager,
  existingSession?: Session,
): Promise<MainAgentResult> {
  // 初始化 WorkflowController
  const workflowController = context.workflowController ?? createWorkflowController({
    plannerAgent: createPlannerAgent({
      llmConfig: context.llmConfig,
      artifactManager: new ChangeArtifactsManager(context.worktreePath),
    }),
    reviewerAgent: createReviewerAgent({
      llmConfig: context.llmConfig,
    }),
    artifactManager: new ChangeArtifactsManager(context.worktreePath),
    worktreePath: context.worktreePath,
  });
  
  // ... rest of initialization ...
}
```

2. 修改 System Prompt，让它调用 WorkflowController：

```
## Workflow Execution

To execute a workflow stage:
1. Call `execute_stage` tool with the current stage
2. If result.requiresApproval is true, use `ask_user` to get approval
3. Based on user decision, either advance stage or request changes
```

3. 添加 `execute_stage` tool：

```typescript
function createExecuteStageTool(options: {
  workflowController: WorkflowController;
  issue: Issue;
}) {
  return {
    name: 'execute_stage',
    description: 'Execute the current workflow stage',
    parameters: z.object({}),
    execute: async () => {
      return await options.workflowController.executeStage(
        options.issue,
        options.issue.stage
      );
    },
  };
}
```

**相关任务**: T-008, T-008-2

---

## Implementation Checklist

### Critical (Blocking)
- [ ] **T-004-1**: Fix Planner Agent file generation
- [ ] **T-007**: Implement user approval interface  
- [ ] **T-008**: Integrate Main Agent with WorkflowController
- [ ] **T-008-1**: Create write_file tool

### Important
- [ ] **T-005-1**: Add task status tracking
- [ ] **T-006-1**: Improve Reviewer test execution
- [ ] **T-008-2**: Refactor Main Agent initialization

### Nice to Have
- [ ] **T-004-2**: Extract Planner Prompt to YAML
- [ ] **T-006-2**: Extract Reviewer Prompt to YAML

---

## Testing Checklist

### Unit Tests
- [ ] Planner Agent generates valid artifacts
- [ ] Reviewer Agent correctly parses dimensions from prompt
- [ ] Build phase updates task status correctly
- [ ] WorkflowController validates stage transitions

### Integration Tests
- [ ] End-to-end Plan → Build → Review workflow
- [ ] User approval flow (approve/reject/abort)
- [ ] Task failure and retry logic
- [ ] File generation and artifact management

### Manual Tests
- [ ] Create issue and run Plan phase
- [ ] Review generated artifacts
- [ ] Run Build phase with multiple tasks
- [ ] Review code changes
- [ ] Test error scenarios (network failure, invalid config, etc.)

---

## Design Decisions Log

### Decision: How should Planner generate files?

**Options considered**:
1. Agent writes directly via tool
2. Agent returns content, code writes
3. Hybrid (Agent suggests, code confirms)

**Decision**: Option 2 (Agent returns, code writes)

**Rationale**:
- Better control over file locations
- Can validate content before writing
- Easier to debug
- Consistent with existing patterns

**Date**: 2026-04-09

---

### Decision: Should we support user-customizable prompts?

**Options considered**:
1. Hardcoded prompts only
2. File-based prompts with defaults
3. Fully dynamic (runtime loaded)

**Decision**: Option 2 (File-based with defaults)

**Rationale**:
- Flexibility for power users
- Backward compatible (defaults work out of box)
- Easy to iterate on prompts
- Can version control custom prompts

**Date**: 2026-04-09

---

## Open Questions

1. **Q**: How to handle long-running Agent operations (timeout)?
   **A**: [Pending] Need to add timeout handling in streamText calls

2. **Q**: Should we cache Agent responses for replay/debugging?
   **A**: [Pending] Could add to session memory

3. **Q**: How to handle partial failures (some tasks pass, some fail)?
   **A**: [Pending] Current design stops on first failure, might want resume capability

4. **Q**: Should Reviewer Agent auto-fix issues or just report?
   **A**: [Pending] Current design only reports, user decides
