## ADDED Requirements

### Requirement: Issue create and update accept IsDraft

`POST /api/issues` and `PATCH /api/issues/:number` SHALL accept an `isDraft` boolean. When `isDraft` is omitted on create, the created Issue SHALL default to `isDraft = true` (draft). Create and update responses SHALL include the resulting `isDraft`, `canStart`, and `blocker` fields.

#### Scenario: Create issue defaults to draft

- **WHEN** a client sends `POST /api/issues` without an `isDraft` field
- **THEN** the API creates the Issue with `isDraft = true`
- **AND** the response includes `isDraft: true`, `canStart: false`, and `blocker` of `Draft`

#### Scenario: Create issue explicitly ready

- **WHEN** a client sends `POST /api/issues` with `isDraft: false`
- **THEN** the API creates the Issue with `isDraft = false`
- **AND** the response includes `isDraft: false` and a `canStart` / `blocker` derived from its prerequisites

#### Scenario: Update issue draft state

- **WHEN** a client sends `PATCH /api/issues/:number` with `isDraft: false`
- **THEN** the API updates the Issue's `IsDraft` flag
- **AND** the response includes the updated `isDraft`, `canStart`, and `blocker`

## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态。Issue list and detail responses SHALL include structured start-readiness data (`isDraft`, `canStart`, and `blocker`) so clients do not parse issue body text. The responses SHALL NOT include a `startEligibility` object or a `waitingForDelivery` field.

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
- **AND** the response includes `prerequisites`, `isDraft`, `canStart`, and `blocker`

#### Scenario: List Issues includes start readiness
- **WHEN** a client requests `GET /api/issues`
- **THEN** each Issue item includes `prerequisites`, `isDraft`, `canStart`, and `blocker`
- **AND** if the Issue is waiting for prerequisite delivery, `blocker` is `WaitingFor(Issue)` identifying the prerequisite issue numbers

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start。The same start handler SHALL also enforce the Issue's start readiness — it SHALL refuse a draft Issue and an Issue with an undelivered prerequisite — before enqueueing work, reporting the concrete `blocker`.

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start draft issue is rejected
- **WHEN** a client requests `POST /api/issues/:number/start`
- **AND** the Issue has `isDraft = true`
- **THEN** server returns a 400-class response
- **AND** the response reports a `blocker` of `Draft`
- **AND** the response message is equivalent to `Issue #N is still a draft`
- **AND** server SHALL NOT enqueue `start-pipeline`

#### Scenario: Start ready active issue in backlog stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `backlog`
- **AND** the Issue has `isDraft = false` and no undelivered prerequisites
- **THEN** 正常启动 agent

#### Scenario: Start issue waiting for prerequisite delivery
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** Issue #201 has prerequisite issue #200
- **AND** Issue #200 is not delivered
- **THEN** server returns a 400-class response
- **AND** the response includes an actionable message equivalent to `Issue #201 is waiting for prerequisite #200 to be delivered.`
- **AND** the response includes structured `canStart: false` and `blocker` of `WaitingFor(Issue)` identifying Issue #200
- **AND** server SHALL NOT enqueue `start-pipeline`

#### Scenario: Start issue after prerequisites delivered
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** every prerequisite issue for Issue #201 is delivered
- **AND** Issue #201 has `isDraft = false` and otherwise satisfies the existing start checks
- **THEN** server enqueues `start-pipeline`
- **AND** returns the existing accepted start response

### Requirement: API accepts issue start prerequisite declarations

The HTTP API SHALL provide a structured way to declare that an Issue has a prerequisite issue that must be delivered before start. Declaration requests SHALL identify Issues by structured fields rather than requiring body text parsing.

#### Scenario: Declare start prerequisite
- **WHEN** a client declares that Issue #201 requires Issue #200 before start
- **THEN** the API records Issue #200 as a prerequisite issue for Issue #201
- **AND** the response includes updated `prerequisites`, `isDraft`, `canStart`, and `blocker` for Issue #201

#### Scenario: Reject circular start prerequisite declaration
- **WHEN** declaring a start prerequisite would make an Issue directly or indirectly require itself before start
- **THEN** the API returns a 400-class response
- **AND** the response reason is `circular-prerequisite`
- **AND** the rejected prerequisite is not recorded

### Requirement: API represents start readiness with derived canStart and blocker

The HTTP API SHALL name issue start-readiness response fields using `isDraft`, `canStart`, and `blocker`, where `blocker` is `Draft`, `WaitingFor(Issue)`, or none. The API SHALL NOT expose start readiness through a `startEligibility` object, a `Reason` string, a `Message` string, a `waitingForDelivery` field, a legacy dependency-status, or a blocked-start response model.

#### Scenario: Response describes a draft issue
- **WHEN** an Issue has `IsDraft = true`
- **THEN** the API response includes `isDraft: true`, `canStart: false`, and `blocker` of `Draft`
- **AND** the API response SHALL NOT include `startEligibility` or `waitingForDelivery`

#### Scenario: Response describes a waiting issue
- **WHEN** a ready Issue is waiting for prerequisite issue #200 to be delivered
- **THEN** the API response includes `isDraft: false`, `canStart: false`, and `blocker` of `WaitingFor(Issue)` identifying Issue #200
- **AND** each prerequisite entry indicates whether its prerequisite issue is delivered
- **AND** the API response SHALL NOT include `startEligibility` or `waitingForDelivery`

#### Scenario: Response describes a startable issue
- **WHEN** a ready Issue has all prerequisites delivered
- **THEN** the API response includes `canStart: true` and `blocker` of none
