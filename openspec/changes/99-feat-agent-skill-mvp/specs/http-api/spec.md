## ADDED Requirements

### Requirement: API 提供 Skill 管理接口

Server SHALL 提供 `/api/skills` 路由组，基于 Hono 框架实现。API handler SHALL 通过 SkillService 操作数据，包含 3 个端点：列出 skills、触发执行、查看执行历史。

#### Scenario: 路由注册

- **WHEN** server 启动并注册 skill 路由
- **THEN** `createSkillRoutes` 接收 `skillService` 和 `projectService` 参数
- **AND** handler 中不直接调用数据库 repo

#### Scenario: 列出 skills

- **WHEN** 请求 `GET /api/skills`
- **THEN** 通过 SkillService 返回当前项目的所有已注册 skills
- **AND** 返回 200

#### Scenario: 触发 skill 执行

- **WHEN** 请求 `POST /api/skills/:name/run`
- **THEN** 通过 SkillService 触发 skill 执行
- **AND** 返回 202 Accepted

#### Scenario: 查询执行历史

- **WHEN** 请求 `GET /api/skills/:name/runs`
- **THEN** 通过 SkillService 返回执行历史
- **AND** 返回 200
