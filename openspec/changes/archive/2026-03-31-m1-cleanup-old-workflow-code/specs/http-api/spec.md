## MODIFIED Requirements

### Requirement: API 提供操作接口

Server SHALL 提供 RESTful API 供 CLI 执行操作。

#### Scenario: 启动 Issue 处理
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **THEN** Main Agent 被启动处理该 Issue
- **AND** 返回 Issue 信息和运行状态

## REMOVED Requirements

### Requirement: 暂停 Issue
**Reason**: M1 does not support pause. The pause endpoint was already returning 501. Pause will be re-implemented in M2 with AbortController support.
**Migration**: Stop the mo-server process to halt a running agent.

### Requirement: API provides operation interface
**Reason**: The pause endpoint (501) and the approve/resume endpoints (old 6-stage workflow) are all dead code in M1. No M1 code path reaches them.
**Migration**: No migration needed. These endpoints were never functional in M1.

### Requirement: start endpoint uses type-safe enum for error handling
**Reason**: This requirement is already satisfied and stable. It was a one-time fix, not a behavioral spec to maintain long-term.
**Migration**: No migration needed. The fix remains in place.

### Requirement: status API uses correct brand name
**Reason**: Already satisfied and stable. Brand name is correct everywhere.
**Migration**: No migration needed.

## ADDED Requirements

### Requirement: Status API reflects M1 stage model
The status API SHALL only report stages used in M1: draft, designing, implementing, done. The response SHALL NOT include task-related fields (runningTasks, queuedTasks, activeWorkers) or waiting-stage counts (waitingDesignReview, waitingReview). The `ServerState` interface SHALL NOT contain `activeTasks` or `queuedTasks` fields.

#### Scenario: Get current project status
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** the response SHALL include `issuesByStage` with only `draft`, `designing`, `implementing`, `done` counts
- **AND** the response SHALL NOT include `runningTasks`, `queuedTasks`, or `activeWorkers`

#### Scenario: ServerState has no task fields
- **WHEN** the ServerState interface is inspected
- **THEN** it SHALL NOT contain `activeTasks` or `queuedTasks`

#### Scenario: Issue show endpoint omits stale fields
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** the response SHALL NOT include `progress` or `stageInfo` fields
- **AND** the issue's current stage SHALL still be available in `issue.stage`

### Requirement: Removed endpoints return 404
Endpoints that are removed (approve, resume, pause) SHALL return HTTP 404 instead of their previous behavior.

#### Scenario: Removed endpoint accessed
- **WHEN** a request is made to `POST /api/issues/:number/approve`, `POST /api/issues/:number/resume`, or `POST /api/issues/:number/pause`
- **THEN** the response SHALL have status code 404
