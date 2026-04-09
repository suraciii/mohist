## Context

The `implement-mohist-workflow` change has established the foundation of the new Mohist workflow system with three core Agents (Planner, Coder, Reviewer) and a simplified stage model (Explore → Plan → Build → Review → Done). However, critical gaps remain in task tracking, test execution, prompt management, and user approval.

This change focuses on completing the workflow implementation by addressing these gaps with production-ready solutions.

## Goals / Non-Goals

**Goals:**
- Implement reliable task status tracking during Build phase
- Improve Reviewer Agent to prioritize test execution
- Extract Reviewer Agent prompt to YAML for customization
- Design and implement a reliable user approval mechanism
- Ensure all components are testable and maintainable

**Non-Goals:**
- Not redesigning the overall workflow architecture
- Not adding new workflow stages
- Not changing the core Agent behaviors (Planner/Coder/Reviewer)
- Not implementing a general-purpose workflow engine

## Decisions

### Decision 1: Task Status Storage Location

**Options Considered:**
1. Store status in prd.json alongside task definitions
2. Store status in separate task-status.json file
3. Store status in SQLite database

**Decision**: Option 1 (prd.json)

**Rationale:**
- Single source of truth for task information
- Atomic updates (prd.json is already read/written as a unit)
- Simple implementation - no new file management
- Easy to inspect manually
- Git version control captures status history

**Implementation:**
```json
{
  "tasks": [
    {
      "id": "T-001",
      "title": "...",
      "status": "completed",
      "startedAt": "2026-04-09T10:00:00Z",
      "completedAt": "2026-04-09T10:05:00Z",
      "attempts": 1
    }
  ]
}
```

### Decision 2: Reviewer Test Execution Priority

**Execution Order:**
1. Check `package.json` exists
2. Parse scripts section
3. If `test` script exists and is not default (not containing "no test specified"):
   - Run `npm test`
4. Else if `build` script exists:
   - Run `npm run build`
5. Else:
   - Skip test execution (return skipped status)

**Error Handling:**
- Test/build failures are reported as correctness issues
- Include last 1000 characters of output in error message
- Distinguish between execution errors (command not found) and test failures

### Decision 3: Prompt File Structure

Following the pattern established by T-004-2:

```
src/agents/prompts/
├── planner-default.yaml      ✅ (exists)
├── planner-self-review.yaml  ✅ (exists)
└── reviewer-default.yaml     🆕 (new)
```

**Reviewer Prompt YAML Structure:**
```yaml
role: reviewer
name: Mohist Reviewer
description: Reviews code quality across multiple dimensions

dimensions:
  correctness:
    description: Code correctness and quality
    checks:
      - name: Logic errors
        severity: error
      - name: Type safety
        severity: error
      - name: Lint violations
        severity: warning
  
  complexity:
    description: Code complexity metrics
    checks:
      - name: Function length
        severity: warning
        threshold: 50
      - name: Cyclomatic complexity
        severity: warning
        threshold: 10
  
  test_coverage:
    description: Test coverage and quality
    checks:
      - name: Tests exist
        severity: error
      - name: Tests pass
        severity: error
      - name: Coverage adequate
        severity: warning
        threshold: "80%"
  
  security:
    description: Security best practices
    checks:
      - name: Input validation
        severity: error
      - name: Injection risks
        severity: error
      - name: Sensitive data exposure
        severity: warning

output_format:
  passed: boolean
  dimensions: array
  overall_reasoning: string
  fix_suggestions: array
```

### Decision 4: User Approval Architecture

**Problem with Current Approach:**
The current implementation relies on the LLM to:
1. Parse the JSON result from `execute_stage`
2. Recognize `requiresApproval: true`
3. Call `ask_user` tool
4. Parse user response
5. Call `advance_stage` or retry

This is fragile because the LLM might miss any of these steps.

**Proposed Solution: Approval-Aware Execute Stage**

Modify the `execute_stage` tool to handle approval internally:

```typescript
// execute-stage.ts
execute: async (params) => {
  const result = await workflowController.executeStage(issue, stage);
  
  if (result.requiresApproval) {
    // Present results and get user approval
    const approvalResult = await presentAndGetApproval(result);
    
    if (approvalResult.decision === 'approve') {
      return {
        success: true,
        approved: true,
        message: 'User approved, proceeding to next stage',
        nextAction: 'advance_stage'
      };
    } else if (approvalResult.decision === 'changes') {
      return {
        success: false,
        approved: false,
        message: 'User requested changes',
        nextAction: 'retry_stage'
      };
    } else {
      return {
        success: false,
        approved: false,
        message: 'User aborted workflow',
        nextAction: 'abort'
      };
    }
  }
  
  return result;
}
```

**Benefits:**
- Single tool call handles entire stage execution including approval
- No reliance on LLM to coordinate multiple tool calls
- Clear, structured response
- Main Agent just needs to check `nextAction` field

**Trade-offs:**
- Tool becomes more complex (violates single responsibility)
- Tightly couples execution and approval
- Harder to customize approval UI

**Alternative: Keep Separation, Add Reliability**

Instead of merging execution and approval, add explicit state tracking:

1. `execute_stage` returns with `status: "awaiting_approval"`
2. Main Agent system prompt explicitly instructs to call `submit_approval`
3. `submit_approval` tool validates the pending approval exists
4. If validation fails, return error (can't silently skip approval)

This maintains separation while preventing silent failures.

**Decision**: Use Alternative (Keep Separation, Add Reliability)

**Rationale:**
- Maintains tool separation of concerns
- Explicit state prevents silent failures
- Easier to test each component independently
- Can customize approval presentation per stage

## Risks / Trade-offs

### Risk 1: Task Status Updates May Fail Mid-Build

**Scenario**: Build phase updates task status to "in_progress", then task fails, but status update to "failed" fails due to disk error.

**Mitigation:**
- Wrap status updates in try-catch with logging
- On resume, check if any task is stuck in "in_progress" (indicates previous failure)
- Allow manual status reset via CLI command

### Risk 2: npm test May Hang

**Scenario**: `npm test` runs indefinitely (e.g., waiting for user input, infinite loop in tests).

**Mitigation:**
- Set timeout for test execution (5 minutes default)
- Report timeout as correctness failure
- Allow timeout configuration per project

### Risk 3: Prompt YAML File Missing or Corrupted

**Scenario**: `reviewer-default.yaml` is deleted or has syntax errors.

**Mitigation:**
- Maintain embedded default as fallback
- Validate YAML on load, fallback to embedded if invalid
- Log warning when using fallback

### Risk 4: User Approval State Synchronization

**Scenario**: execute_stage returns awaiting_approval, but Main Agent crashes before calling submit_approval. On restart, state is unclear.

**Mitigation:**
- Persist approval state in SQLite (issue table)
- On Main Agent restart, check for pending approvals
- Resume from approval state if found

## Migration Plan

### Phase 1: Task Status Tracking (T-005-1)
1. Add updateTaskStatus method to ChangeArtifactsManager
2. Modify executeBuildStage to update status before/after each task
3. Add resume capability (skip completed tasks)
4. Test with sample build

### Phase 2: Reviewer Improvements (T-006-1, T-006-2)
1. Implement test priority logic in ReviewerAgent
2. Create reviewer-default.yaml
3. Update prompt-loader.ts
4. Test Reviewer with projects that have tests vs. those that don't

### Phase 3: User Approval (T-007)
1. Add approval state to Issue type
2. Create submit_approval tool
3. Update execute_stage to set approval state
4. Modify Main Agent system prompt for approval flow
5. Add resume from approval state
6. End-to-end test with user interactions

## File Structure

```
packages/cli/src/
├── agents/
│   ├── reviewer-agent.ts          📝 (modify - test execution)
│   ├── prompt-loader.ts           📝 (modify - add reviewer prompt loading)
│   └── prompts/
│       └── reviewer-default.yaml  🆕 (new)
├── workflow/
│   └── workflow-controller.ts     📝 (modify - task status updates)
├── artifacts/
│   └── change-artifacts-manager.ts 📝 (modify - add updateTaskStatus)
├── tools/
│   ├── execute-stage.ts           📝 (modify - approval state)
│   └── submit-approval.ts         🆕 (new)
└── types/
    └── index.ts                   📝 (modify - add approval state to Issue)
```

## Implementation Notes

### T-005-1: Task Status Update Implementation

```typescript
// In ChangeArtifactsManager
updateTaskStatus(
  issueNumber: number, 
  taskId: string, 
  status: TaskStatus
): void {
  const prd = this.readPrd(issueNumber);
  if (!prd) return;
  
  const task = prd.tasks.find(t => t.id === taskId);
  if (!task) return;
  
  // Add status fields
  (task as any).status = status.status;
  if (status.startedAt) (task as any).startedAt = status.startedAt;
  if (status.completedAt) (task as any).completedAt = status.completedAt;
  if (status.attempts) (task as any).attempts = status.attempts;
  if (status.error) (task as any).error = status.error;
  
  this.writePrd(issueNumber, prd);
}
```

### T-006-1: Test Execution Implementation

```typescript
// In ReviewerAgent
private async runTests(worktreePath: string): Promise<TestResult> {
  const packageJsonPath = path.join(worktreePath, 'package.json');
  
  if (!fs.existsSync(packageJsonPath)) {
    return { passed: true, skipped: true, reason: 'No package.json' };
  }
  
  const packageJson = JSON.parse(
    fs.readFileSync(packageJsonPath, 'utf-8')
  );
  const scripts = packageJson.scripts || {};
  
  // Try test first
  if (scripts.test && !scripts.test.includes('no test specified')) {
    try {
      const output = execSync('npm test', {
        cwd: worktreePath,
        encoding: 'utf-8',
        timeout: 300000,
      });
      return { passed: true, output };
    } catch (error) {
      return {
        passed: false,
        issues: [{
          location: 'test',
          message: 'Tests failed',
          suggestion: this.extractErrorOutput(error)
        }]
      };
    }
  }
  
  // Fallback to build
  if (scripts.build) {
    // ... existing build logic
  }
  
  return { passed: true, skipped: true, reason: 'No test or build script' };
}
```

### T-007: Approval State Implementation

```typescript
// New type
interface Issue {
  // ... existing fields
  approvalState?: {
    stage: Stage;
    status: 'awaiting' | 'approved' | 'rejected';
    output: unknown;
    requestedAt: string;
    respondedAt?: string;
  };
}

// execute_stage tool modification
if (result.requiresApproval) {
  // Persist approval state
  await issueRepo.setApprovalState(issue.id, {
    stage: params.stage,
    status: 'awaiting',
    output: result.output,
    requestedAt: new Date().toISOString()
  });
  
  return {
    status: 'awaiting_approval',
    stage: params.stage,
    summary: this.buildApprovalSummary(result),
    message: 'Stage execution complete, awaiting user approval'
  };
}
```

## Testing Strategy

### Unit Tests
- ChangeArtifactsManager.updateTaskStatus
- ReviewerAgent.runTests with various package.json configurations
- PromptLoader with missing/corrupted files
- Approval state transitions

### Integration Tests
- Build phase with multiple tasks, verify status updates
- Reviewer phase with test vs. build fallback
- Complete approval flow (approve/reject/abort)

### Manual Tests
1. Create issue, run Plan, verify approval request
2. Approve plan, run Build, verify task status progression
3. Fail a task, verify "failed" status and error message
4. Resume build from failed task
5. Run Review on code with failing tests
6. Run Review on code without tests (should use build)
