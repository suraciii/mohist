## ADDED Requirements

### Requirement: 前端通过 React Query mutation 管理项目状态变更

WebUI SHALL 使用 TanStack React Query 的 `useMutation` hook 封装项目创建、删除和切换操作，mutation 成功后 SHALL invalidate `['projects']` query cache。

#### Scenario: 创建项目 mutation 成功后刷新列表
- **WHEN** `createProject` mutation 成功
- **THEN** 自动 invalidate `['projects']` query
- **AND** 项目列表自动重新获取

#### Scenario: 删除项目 mutation 成功后刷新列表
- **WHEN** `deleteProject` mutation 成功
- **THEN** 自动 invalidate `['projects']` query
- **AND** 项目列表自动重新获取
