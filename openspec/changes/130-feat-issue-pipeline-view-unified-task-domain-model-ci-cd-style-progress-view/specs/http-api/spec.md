## ADDED Requirements

### Requirement: API 提供 Issue Stage Executions 查询接口

Server SHALL 提供 `GET /api/issues/:number/executions` 端点，返回指定 Issue 的所有 stage execution 记录。

#### Scenario: 查询 active issue 的 executions
- **WHEN** CLI 请求 `GET /api/issues/:number/executions`
- **THEN** 返回该 issue 的所有 stage execution 记录，按 `createdAt` 升序排列
- **AND** 每条记录包含 `taskResults` (StageTaskResult[]) 和 `checkResults` (CheckResult[])

#### Scenario: 查询不存在 issue 的 executions
- **WHEN** CLI 请求 `GET /api/issues/999/executions`
- **AND** issue #999 不存在
- **THEN** 返回 404 错误
