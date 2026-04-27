## ADDED Requirements

### Requirement: Pipeline 可被外部中断

Pipeline SHALL 支持在任何 stage（plan, build, check）被外部 stop 请求中断。中断后 issue status 变为 `blocked`，pipeline 的内部 promise 被 resolve（outcome 为 cancelled）。

#### Scenario: 在 plan stage 中断 pipeline
- **WHEN** issue 在 plan stage 执行中
- **AND** 用户请求 stop
- **THEN** ACP session 被 cancel
- **AND** pipeline promise 以 cancelled 结束
- **AND** issue status 变为 `blocked`

#### Scenario: 在 build stage 中断 pipeline
- **WHEN** issue 在 build stage 执行中
- **AND** 用户请求 stop
- **THEN** ACP session 被 cancel
- **AND** pipeline promise 以 cancelled 结束
- **AND** issue status 变为 `blocked`

#### Scenario: 在 check stage 中断 pipeline
- **WHEN** issue 在 check stage 执行中
- **AND** 用户请求 stop
- **THEN** ACP session 被 cancel
- **AND** pipeline promise 以 cancelled 结束
- **AND** issue status 变为 `blocked`

#### Scenario: 中断后可 reopen
- **WHEN** pipeline 被中断，issue status 为 `blocked`
- **AND** 用户请求 reopen
- **THEN** issue 恢复到之前的 stage
- **AND** pipeline 可重新启动
