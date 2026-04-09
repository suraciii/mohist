## ADDED Requirements

### Requirement: Unified Workflow Result Interfaces

#### Scenario: Single Source of Truth for Types
- **GIVEN** `PlanResult` and `ReviewResult` are defined in multiple files
- **WHEN** components communicate
- **THEN** they should use identical, shared interfaces

**Acceptance Criteria:**
1. Create `types/workflow-results.ts` as single source of truth
2. Define `PlanResult` interface:
   - `success: boolean` - Required
   - `changePath: string` - Required on success
   - `artifacts: PlanArtifacts` - Required on success
   - `selfReviewNotes?: string` - Optional
   - `iterations: number` - Required
   - `duration: number` - Required
   - `error?: string` - Required on failure
3. Define `ReviewResult` interface:
   - `passed: boolean` - Required
   - `dimensions: DimensionResult[]` - Required
   - `overallReasoning: string` - Required
   - `fixSuggestions?: string[]` - Optional
   - `duration: number` - Required
4. Export all related types (`PlanArtifacts`, `DimensionResult`, etc.)
5. Update all imports across codebase

**Rationale:**
- Prevents interface drift
- Easier maintenance
- Clear contracts between components

---

### Requirement: Stage Transition Rules Unification

#### Scenario: Consistent Stage Transitions
- **GIVEN** the system has multiple places defining stage transitions
- **WHEN** an agent tries to advance an issue from one stage to another
- **THEN** the transition rules should be consistent across all components

**Acceptance Criteria:**
1. Single source of truth for `STAGE_TRANSITIONS` in `types/index.ts`
2. `advance-stage.ts` imports and uses `isValidTransition()` from types
3. Remove hardcoded `M1_ALLOWED_TRANSITIONS` from advance-stage.ts
4. Support both new and old stage flows:
   - New: Explore → Plan → Build → Review → Done
   - Old: Draft → Plan → Build → Check → Done
5. All valid transitions tested

**Current Stage Transitions:**
```typescript
const STAGE_TRANSITIONS: Record<Stage, Stage[]> = {
  [Stage.Explore]: [Stage.Plan],
  [Stage.Plan]: [Stage.Build],
  [Stage.Build]: [Stage.Review],
  [Stage.Review]: [Stage.Done, Stage.Build],  // Allow regression
  [Stage.Done]: [],
  [Stage.Draft]: [Stage.Plan],  // Backward compatibility
  [Stage.Check]: [Stage.Done, Stage.Plan],  // Backward compatibility
};
```

---

### Requirement: Build Stage OpenSpec Integration

#### Scenario: Unified Build Execution
- **GIVEN** Build stage may have OpenSpec tasks in prd.json
- **WHEN** executing Build stage
- **THEN** it should use RalphExecutor for OpenSpec tasks

**Acceptance Criteria:**
1. `executeBuildStage` checks for prd.json in change directory
2. If prd.json exists with tasks:
   - Detect OpenSpec change using `detectOpenSpecChange()`
   - Create RalphExecutor with proper context
   - Execute tasks via `RalphExecutor.execute()`
   - Handle `RalphLoopResult` (success, failed, paused states)
3. If no prd.json:
   - Fall back to current spawn_coder behavior
4. Task status properly tracked and persisted
5. Support task failure with retry

**Integration Approach:**
```typescript
private async executeBuildStage(issue: Issue): Promise<StageResult> {
  const prd = this.artifactManager.readPrd(issue.number);
  
  if (prd?.tasks?.length > 0) {
    const change = detectOpenSpecChange(this.worktreePath, issue);
    if (change) {
      const executor = new RalphExecutor({
        worktreePath: this.worktreePath,
        projectPath: this.worktreePath,
        issueId: issue.id,
        onAskUser: async (question, taskId) => {
          // Trigger pause and wait for user
          this.eventBus?.emit('ask_user', { issueId: issue.id, question, taskId });
          return await this.waitForUserResponse(issue.id);
        }
      });
      
      const result = await executor.execute(change);
      return {
        success: result.success,
        requiresApproval: result.paused || !result.success,
        output: result,
      };
    }
  }
  
  // Non-OpenSpec mode - use spawn_coder
  // ... existing logic
}
```

---

### Requirement: Issue-Level Approval State Query

#### Scenario: Check Approval Status by Issue
- **GIVEN** need to check if specific issue has pending approval
- **WHEN** querying approval state
- **THEN** should query by issue id, not project id

**Acceptance Criteria:**
1. Add `findPendingApprovalByIssueId(issueId: string)` method to IssueRepo
2. Method queries: `SELECT * FROM issues WHERE id = ? AND approval_state IS NOT NULL`
3. Parse approval_state JSON and check status === 'awaiting'
4. Return Issue | null
5. Add unit tests

**Current Problem:**
```typescript
// Current: queries by projectId, returns first matching
findPendingApproval(projectId: string): Issue | null

// Needed: queries by issueId
findPendingApprovalByIssueId(issueId: string): Issue | null
```

---

### Requirement: Duplicate Execution Prevention

#### Scenario: Prevent Starting Agent When Approval Pending
- **GIVEN** an issue has pending approval
- **WHEN`AgentRunnerService.start()` is called
- **THEN** it should reject with error instead of starting

**Acceptance Criteria:**
1. In `AgentRunnerService.start()`, before executing:
   - Call `issueRepo.findPendingApprovalByIssueId(issue.id)`
   - If pending approval exists:
     - Return `{ started: false, error: 'Issue has pending approval' }`
     - Error message includes instructions to use resume or submit approval
2. Prevent duplicate agent execution for same issue
3. Log attempt for debugging

**Implementation:**
```typescript
start(issue: Issue, ...): { started: boolean; error?: string; queuePosition?: number } {
  // Check if already running
  if (this.activeAgents.has(issue.id)) {
    return { started: false, error: 'Issue is already being processed' };
  }
  
  // Check for pending approval
  const pendingApproval = this.issueRepo?.findPendingApprovalByIssueId(issue.id);
  if (pendingApproval?.approvalState?.status === 'awaiting') {
    return { 
      started: false, 
      error: `Issue #${issue.number} has pending approval for stage ${pendingApproval.approvalState.stage}. ` +
             `Use 'mo issue resume ${issue.number}' or submit approval first.`
    };
  }
  
  // Continue with normal execution...
}
```

---

### Requirement: Workflow Configuration Validation

#### Scenario: Ensure Correct Approval Configuration
- **GIVEN** workflow.yaml defines stages
- **WHEN** AgentRunnerService checks if should pause
- **THEN** it should find correct approval configuration

**Acceptance Criteria:**
1. Review current `workflow.yaml` configuration
2. Ensure stages have correct `approval` settings:
   - Stages that need user approval: `approval: true`
   - Stages that auto-advance: `approval: false`
3. Support both new stages (Explore, Review) and old stages (Draft, Check)
4. Document expected configuration

**Expected Configuration:**
```yaml
stages:
  # New workflow
  - stage: explore
    prompt: "Explore the requirements..."
    approval: false
  - stage: plan
    prompt: "Generate design artifacts..."
    approval: true  # Pause after plan for approval
  - stage: build
    prompt: "Execute build..."
    approval: false
  - stage: review
    prompt: "Review code..."
    approval: true  # Pause after review for approval
  - stage: done
    prompt: "Complete"
    approval: false
    
  # Old workflow (backward compatibility)
  - stage: draft
    prompt: "Draft issue..."
    approval: false
  - stage: check
    prompt: "Check implementation..."
    approval: true
```

**Note:** AgentRunnerService.shouldPauseAtCurrentStage() checks if NEXT stage has approval: true
