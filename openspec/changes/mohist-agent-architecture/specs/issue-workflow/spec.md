## REMOVED Requirements

### Requirement: Issue 工作流状态机
**Reason**: The hardcoded state machine (STAGE_TRANSITIONS map) is replaced by workflow.yaml-driven stages. Stage definitions come from `.mohist/workflow.yaml`, not from `types/index.ts`. Stage transitions are decided by the Main Agent's LLM, not by fixed rules.
**Migration**: Remove hardcoded STAGE_TRANSITIONS from `workflow/issue-workflow.ts`. Stages are now defined in workflow.yaml.

### Requirement: 用户可以暂停
**Reason**: Pause/resume is now handled by the Main Agent session's gate mechanism. When a sub-agent is cancelled (user sends rollback or pause command), the Main Agent handles it via LLM decision-making.
**Migration**: User pause command triggers a user_command event on the bus, handled by the Main Agent.

### Requirement: 用户可以恢复
**Reason**: Recovery is now handled by session restoration from SQLite. On server restart, Main Agent sessions are restored and the LLM re-evaluates the current state.
**Migration**: Session persistence replaces manual resume logic.
