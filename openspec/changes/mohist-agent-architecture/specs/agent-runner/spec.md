## REMOVED Requirements

### Requirement: Agent Runner 可以 spawn opencode agents
**Reason**: The AgentRunner with static prompts and fixed agent types is replaced by the sub-agent system. Sub-agents are now spawned by the Main Agent via the `spawn_agent` tool, each with its own LLM loop, prompt, and tool set.
**Migration**: Remove `agent/runner.ts` and `agent/prompts.ts`. Agent spawning is now handled by the agent-runtime's sub-agent spawning capability.

### Requirement: Agent Runner 监控 agent 执行状态
**Reason**: Sub-agent lifecycle is now managed by the agent-runtime. The Main Agent synchronously waits for sub-agent completion (same pattern as opencode's task tool).
**Migration**: Sub-agent completion is handled by the spawn_agent tool's synchronous wait.
