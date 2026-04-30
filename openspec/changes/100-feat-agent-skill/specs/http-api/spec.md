## MODIFIED Requirements

### Requirement: API 提供调度管理接口

Server SHALL 提供 Skill 调度管理的 RESTful API，基于 Hono 框架实现。

#### Scenario: 列出所有调度

- **WHEN** CLI 请求 `GET /api/agent/schedules`
- **THEN** 返回所有 skill 调度列表，每项包含 skill_id、schedule_type、schedule_value、anchor、next_run_at、last_run_at、enabled

#### Scenario: 启用调度

- **WHEN** CLI 请求 `PATCH /api/agent/schedules/:skillId` with `{ enabled: true }`
- **THEN** 调度被启用（`enabled = 1`）
- **AND** `next_run_at` 从当前时间重新计算
- **AND** 新 timer 被设置
- **AND** 返回更新后的调度信息

#### Scenario: 禁用调度

- **WHEN** CLI 请求 `PATCH /api/agent/schedules/:skillId` with `{ enabled: false }`
- **THEN** 调度被禁用（`enabled = 0`）
- **AND** 该调度的 timer 被取消
- **AND** 返回更新后的调度信息

#### Scenario: 调度对应的 skill 不存在

- **WHEN** CLI 请求 `PATCH /api/agent/schedules/:skillId`
- **AND** 该 skillId 没有对应的调度
- **THEN** 返回 404 错误

#### Scenario: 手动刷新调度

- **WHEN** CLI 请求 `POST /api/agent/schedules/refresh`
- **THEN** SchedulerService 重新扫描所有 SKILL.md 的 schedule 配置
- **AND** 更新 `agent_skill_schedules` 表
- **AND** 重置所有 timer
- **AND** 返回刷新结果摘要（created/updated/removed 计数）
