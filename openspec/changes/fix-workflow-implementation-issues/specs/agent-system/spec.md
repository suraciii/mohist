## ADDED Requirements

### Requirement: Conservative Prompt Simplification

#### Scenario: Remove Deprecated References While Keeping Structure
- **GIVEN** Main Agent system prompt is 136 lines with mixed modes
- **WHEN** agent executes
- **THEN** it should receive clear instructions without deprecated references

**Acceptance Criteria:**
1. **Keep** 80%+ of existing prompt content
2. **Remove** references to deprecated tools:
   - Remove `run_ralph_loop` tool references
   - Remove explicit mode detection instructions
3. **Add** clear `execute_stage` usage instructions:
   - When to call execute_stage
   - How to handle stage execution results
   - When to call advance_stage
4. **Keep** OpenSpec-related instructions (still needed for artifact generation)
5. **Test** that agent behavior remains consistent

**Approach:**
```typescript
// Current: Single large prompt
function buildSystemPrompt(issue: Issue, detection: OpenSpecDetection): string {
  // 136 lines mixed content
}

// Revised: Keep structure, remove deprecated parts
function buildSystemPrompt(issue: Issue, detection: OpenSpecDetection): string {
  const basePrompt = `...existing base content...`;
  
  // Remove run_ralph_loop references
  // Keep execute_stage instructions
  // Keep OpenSpec artifact generation instructions
  
  return basePrompt;
}
```

**Content to Remove:**
- "## Ralph Loop (OpenSpec Workflow)" section references to using run_ralph_loop tool
- Mode detection logic that suggests different tools for different modes
- Instructions to "use run_ralph_loop in build stage"

**Content to Keep:**
- "## OpenSpec Plan Stage" section (still needed for artifact format)
- "## How It Works" section with execute_stage
- Tool definitions for execute_stage, submit_approval, advance_stage
- All spawn_coder usage instructions

---

### Requirement: RalphExecutor Pause Integration

#### Scenario: Convert Task-Level Pause to Stage-Level Pause
- **GIVEN** RalphExecutor uses onAskUser callback for task failures
- **WHEN** task fails and requires user decision
- **THEN** it should integrate with AgentRunnerService pause mechanism

**Acceptance Criteria:**
1. RalphExecutor.onAskUser callback triggers AgentRunnerService pause
2. Question and taskId stored for user retrieval
3. AgentRunnerService.resume() provides user response to callback
4. RalphExecutor continues based on user response (retry/skip/abort)

**Flow:**
```
Build Stage
    │──▶ RalphExecutor.execute()
    │       │──▶ Task execution
    │       │──▶ Task fails
    │       │──▶ onAskUser(question, taskId) called
    │       │       │──▶ Emit 'ask_user' event
    │       │       │──▶ Store question/taskId
    │       │       └──▶ Wait for response
    │       │
    └──▶ AgentRunnerService
            │──▶ Detects onAskUser
            │──▶ pause(session)
            │──▶ Wait for resume
            │
User        │──▶ Calls resume with answer
            │
            └──▶ AgentRunnerService
                    │──▶ resume(session)
                    │──▶ Provide answer to onAskUser
                    └──▶ RalphExecutor continues
```

**Implementation Approach:**
Option A: Promise-based wait
```typescript
onAskUser: async (question, taskId) => {
  // Create promise that resolves on resume
  const userResponse = await createUserResponsePromise(issue.id, question, taskId);
  return userResponse;
}
```

Option B: Event-based with state storage
```typescript
// Store pending question
this.pendingUserQuestions.set(issue.id, { question, taskId, resolve });

// In resume, provide answer
const pending = this.pendingUserQuestions.get(issue.id);
if (pending) {
  pending.resolve(userMessage);
  this.pendingUserQuestions.delete(issue.id);
}
```

**Decision:** Use Option B (Event-based) as it fits better with existing architecture.

---

### Requirement: Backward Compatibility for Old Issues

#### Scenario: Support Legacy Stage Names
- **GIVEN** database has issues with Draft/Check stages
- **WHEN** processing these issues
- **THEN** they should work with existing workflow

**Acceptance Criteria:**
1. Draft stage can transition to Plan
2. Check stage can transition to Done or Plan
3. workflow.yaml includes configuration for Draft/Check stages
4. AgentRunnerService.shouldPauseAtCurrentStage handles old stages
5. Tests verify old issue flow works

**Test Cases:**
1. Create issue in Draft stage
2. Execute Draft → Plan transition
3. Progress through to Check stage
4. Execute Check → Done transition
5. Verify approval works at Check stage

---

### Requirement: Agent State Recovery

#### Scenario: Recover from Interrupted Execution
- **GIVEN** agent was paused or crashed
- **WHEN`system restarts
- **THEN`recoverable issues should be detectable

**Acceptance Criteria:**
1. `AgentRunnerService.detectRecoverableIssues()` finds active issues not in Draft/Done
2. Issues with approval_state awaiting are listed as recoverable
3. CLI/API can query recoverable issues
4. User can resume recoverable issues

**Current Implementation:**
```typescript
private detectRecoverableIssues(): RecoverableIssue[] {
  if (!this.issueRepo) return [];
  const activeIssues = this.issueRepo.findAll({ status: IssueStatus.Active });
  return activeIssues
    .filter(issue => issue.stage !== Stage.Draft)
    .map(issue => ({ issueNumber: issue.number, stage: issue.stage }));
}
```

**Enhancement:** Also detect issues with pending approval
