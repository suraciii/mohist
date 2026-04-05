## Requirements

### Requirement: Main Agent 为工作流编排者
Main Agent SHALL 作为 LLM agent 编排者，读取 workflow.yaml 配置，按阶段调用 spawn_coder，传递上下文，推进工作流。Main Agent SHALL 支持 resume 模式——当从 approval gate 恢复时，在已有 session 上继续执行，保留之前的阶段输出和工具调用历史。

#### Scenario: Main Agent 按 workflow.yaml 编排
- **WHEN** `mo issue start 1` 触发 Main Agent，issue 处于 plan 阶段
- **THEN** Main Agent 读取 workflow.yaml，找到 plan 阶段定义（`prompt: "分析..."`），调用 `spawn_coder({ taskTemplate: prompt, variables: { issue: currentIssue } })`，等待结果后调用 `advance_stage({ stage: "build" })`

#### Scenario: Main Agent 传递跨阶段上下文
- **WHEN** plan 阶段完成，Main Agent 进入 build 阶段
- **THEN** Main Agent 在 spawn_coder 的 variables 中包含 plan 阶段返回结果：`{ issue: currentIssue, plan: { output: planResult } }`，spawn_coder 内部完成 prompt 变量替换

#### Scenario: check 失败后回退
- **WHEN** check 阶段 spawn_coder 返回测试失败
- **THEN** Main Agent 自主决定是否回到 build 阶段修复问题，或报告失败

#### Scenario: approval gate 暂停 session
- **WHEN** Main Agent 执行到 approval gate（stage 的下一阶段有 approval: true）
- **THEN** Main Agent 调用 `add_comment` 记录结果后正常结束
- **AND** session 被标记为 paused（不被关闭）
- **AND** session 被存入 AgentRunnerService 的 paused map 等待用户 approve

#### Scenario: resume 后继续执行
- **WHEN** 用户 approve 后 agent 恢复
- **THEN** Main Agent 在已有 session 上继续
- **AND** LLM 看到完整历史（plan 输出、advance_stage 结果、comment）
- **AND** Main Agent 调用 `read_workflow`，找到当前阶段，执行 spawn_coder

### Requirement: Main Agent 通过 context 获取 issue 信息
Main Agent SHALL 通过 `MainAgentContext` 获取当前 issue 完整信息，tools 不需要 `issue_id` 参数。

#### Scenario: tool 无需 issue_id
- **WHEN** Main Agent 调用 `advance_stage({ stage: "build" })`
- **THEN** advance_stage tool 自动操作 context 中的当前 issue，不需要传递 issue_id

### Requirement: Main Agent system prompt 包含 issue 上下文和工作流指导
Main Agent 的 system prompt SHALL 包含 issue 信息、可用工具说明、工作流编排指导。

#### Scenario: system prompt 结构
- **WHEN** Main Agent 启动处理 issue
- **THEN** system prompt 包含：issue 标题/描述/ID、当前阶段、可用工具列表（spawn_coder, advance_stage, add_comment, get_issue, read_workflow）、编排指导（先读 workflow，按阶段执行）

### Requirement: workflow approval 标记不暂停 agent
Main Agent SHALL 将 workflow.yaml 中的 `approval: true` 仅作为标记，执行完该阶段后添加 comment 并主动结束，不自动进入下一阶段。用户通过 `mo issue approve` 恢复执行，agent 在已有 session 上继续。

#### Scenario: approval stage 执行
- **WHEN** Main Agent 执行到 `approval: true` 的阶段
- **THEN** 阶段完成后，Main Agent 调用 `add_comment("阶段完成，等待审批")`，然后正常结束（不报错）

#### Scenario: 用户 approve 恢复执行
- **WHEN** 用户查看结果后执行 `mo issue approve 1`
- **THEN** agent 在已有 session 上恢复执行
- **AND** 进入下一阶段继续编排
