## Why

The `implement-mohist-workflow` change has made significant progress, but several critical gaps remain that prevent the workflow from functioning correctly in production:

### Critical Issues

1. **No Task Status Tracking (T-005-1)**: The Build phase executes tasks from prd.json but doesn't persist their execution status. Users cannot see which tasks are pending, in progress, completed, or failed. This makes it impossible to resume from failures or track progress.

2. **Reviewer Only Runs Build (T-006-1)**: The Reviewer Agent currently only executes `npm run build` to check code correctness. It should prioritize running actual tests (`npm test`) when available, as tests provide much better coverage of code correctness than just compilation.

3. **Reviewer Prompt Embedded in Code (T-006-2)**: The Reviewer Agent's default prompt is hardcoded in TypeScript. Following the pattern established by the Planner Agent, it should be extracted to a YAML file for easier customization.

4. **User Approval Not Integrated (T-007)**: The Plan and Review stages return `requiresApproval: true`, but there's no reliable mechanism to pause execution, present results to the user, and resume based on their decision. The current approach relies on the LLM correctly parsing JSON and calling `ask_user`, which is fragile.

### Why These Matter

Without these fixes:
- **Build phase is a black box** - no visibility into task progress
- **Code quality checks are insufficient** - missing test execution
- **Customization is difficult** - prompts embedded in code
- **User control is unreliable** - approval flow may fail silently

## What Changes

This change implements the remaining functionality to complete the Mohist workflow:

### T-005-1: Task Status Tracking
- Add `status` field to each task in prd.json during Build phase execution
- Track state transitions: `pending` → `in_progress` → `completed`/`failed`
- Record timestamps and attempt counts for each task
- Enable resuming from failed tasks

### T-006-1: Improve Reviewer Test Execution
- Check `package.json` scripts before running tests
- Prioritize `npm test` over `npm run build`
- Handle missing scripts gracefully
- Report test failures as correctness issues with detailed output

### T-006-2: Extract Reviewer Prompt to YAML
- Create `src/agents/prompts/reviewer-default.yaml`
- Add `loadReviewerDefaultPrompt()` function to prompt-loader.ts
- Update ReviewerAgent to load prompt from file
- Maintain backward compatibility with embedded default

### T-007: Implement Reliable User Approval Interface
**Critical Architecture Decision**: Design a reliable mechanism for user approval at Plan and Review stages.

**Options to Evaluate**:
- **Option A**: Keep current approach (LLM parses JSON and calls ask_user)
- **Option B**: execute_stage tool automatically calls ask_user when approval needed
- **Option C**: Add approval state machine layer between Main Agent and WorkflowController

**Decision Criteria**:
- Reliability: Must not fail silently
- Simplicity: Easy to understand and debug
- Testability: Can be unit tested
- User Experience: Clear presentation of approval requests

## Capabilities

### Modified Capabilities

- `workflow-engine`: Add task status tracking during Build phase
- `reviewer-agent`: Improve test execution logic and extract prompt to YAML
- `main-agent`: Integrate reliable user approval flow

### New Capabilities

- `task-status-manager`: Manage task state persistence and queries

## Impact

- **T-005-1**: Modifies `workflow-controller.ts` and `change-artifacts-manager.ts`
- **T-006-1**: Modifies `reviewer-agent.ts` test execution logic
- **T-006-2**: Adds new file `src/agents/prompts/reviewer-default.yaml`
- **T-007**: May require changes to `execute-stage.ts` tool and Main Agent system prompt

## Success Metrics

- Task status updates correctly after each task execution
- Build phase can resume from failed tasks
- Reviewer Agent runs `npm test` when available
- All prompts loadable from YAML files
- User approval flow completes successfully in 100% of cases
- No silent failures in approval process
