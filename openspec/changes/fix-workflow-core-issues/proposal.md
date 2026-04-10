## Why

Mohist workflow has critical integration bugs that prevent end-to-end execution: stage transition rules are duplicated and contradictory, result type interfaces are defined inconsistently across files, the approval query method searches by project instead of by issue, and the Build stage doesn't use the existing RalphExecutor for OpenSpec task execution. Meanwhile, a fully functional pause/resume mechanism already exists in `AgentRunnerService` but is underutilized due to missing workflow.yaml configuration.

## What Changes

- **Unify stage transition rules**: Remove hardcoded `M1_ALLOWED_TRANSITIONS` from `advance-stage.ts`, use `STAGE_TRANSITIONS` from `types/index.ts` as single source of truth
- **Unify result type interfaces**: Create `types/workflow-results.ts` with shared `PlanResult` and `ReviewResult` types, remove duplicate definitions from `workflow-controller.ts`, `planner-agent.ts`, `reviewer-agent.ts`
- **Fix approval state query**: Add `findPendingApprovalByIssueId()` to `IssueRepo` (current method queries by projectId), add duplicate execution prevention in `AgentRunnerService.start()`
- **Integrate RalphExecutor into Build stage**: `executeBuildStage` SHALL detect prd.json and delegate to `RalphExecutor.execute()` for OpenSpec tasks, falling back to spawn_coder otherwise
- **Connect RalphExecutor onAskUser to AgentRunnerService pause**: Task-level user questions SHALL trigger stage-level pause via event-based Promise pattern
- **Validate workflow.yaml approval configuration**: Ensure stages have correct `approval` flags so `shouldPauseAtCurrentStage()` works for both new (Explore/Review) and legacy (Draft/Check) flows
- **Simplify Main Agent prompt**: Remove deprecated `run_ralph_loop` tool references, keep 80%+ of existing prompt content
- **Harden Planner Agent JSON parsing**: Add multi-strategy fallback parsing for LLM artifact output

## Capabilities

### New Capabilities
- `workflow-results`: Unified type definitions for PlanResult and ReviewResult interfaces
- `build-stage-ralph`: Integration of RalphExecutor into the Build stage for OpenSpec task execution

### Modified Capabilities
- `workflow-definition`: Stage transition rules unified to single source, removing M1_ALLOWED_TRANSITIONS duplicate
- `main-agent`: System prompt simplified by removing deprecated tool references
- `local-issue-store`: Approval state query fixed to support issue-level lookup and duplicate execution prevention
- `session-resume`: RalphExecutor onAskUser connected to AgentRunnerService pause/resume mechanism
- `ralph-task-execution`: Task status persistence enhanced for failed task recovery

## Impact

- `packages/cli/src/types/` - New file: `workflow-results.ts`
- `packages/cli/src/workflow/workflow-controller.ts` - Build stage RalphExecutor integration, interface imports
- `packages/cli/src/tools/advance-stage.ts` - Use unified transition rules
- `packages/cli/src/db/issue-repo.ts` - New `findPendingApprovalByIssueId` method
- `packages/cli/src/services/agent-runner-service.ts` - Duplicate execution prevention
- `packages/cli/src/agents/main-agent.ts` - Prompt simplification
- `packages/cli/src/agents/planner-agent.ts` - Interface imports, JSON parsing hardening
- `packages/cli/src/agents/reviewer-agent.ts` - Interface imports
- Backward compatible: Draft/Check stages preserved in transition rules
