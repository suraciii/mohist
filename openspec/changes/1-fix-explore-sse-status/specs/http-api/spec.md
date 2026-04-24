## ADDED Requirements

### Requirement: Explore 会话列表支持 status 过滤

`GET /api/explore` SHALL 支持可选的 `status` 查询参数，用于按会话状态过滤结果。`ExploreSessionRepo.findByProject()` SHALL 接受可选的 `status` 参数，当提供时只返回匹配状态的会话。`ExploreService.listSessions()` SHALL 透传该参数。

#### Scenario: 不传 status 返回全部会话

- **WHEN** 请求 `GET /api/explore?projectId=xxx`（无 status 参数）
- **THEN** 返回该项目下所有状态的会话（active、crystallized、archived）
- **AND** 行为与修改前一致

#### Scenario: 传入 status=active 只返回活跃会话

- **WHEN** 请求 `GET /api/explore?projectId=xxx&status=active`
- **THEN** 只返回 `status = 'active'` 的会话

#### Scenario: 传入 status=crystallized 只返回已结晶会话

- **WHEN** 请求 `GET /api/explore?projectId=xxx&status=crystallized`
- **THEN** 只返回 `status = 'crystallized'` 的会话

#### Scenario: 传入无效 status 值返回空列表

- **WHEN** 请求 `GET /api/explore?projectId=xxx&status=invalid_value`
- **THEN** 返回空数组（无匹配结果）
