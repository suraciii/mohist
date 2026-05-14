## MODIFIED Requirements

### Requirement: API 提供状态查询接口

Server SHALL 提供 RESTful API 供 CLI 查询状态，基于 Hono 框架实现。Issue list and detail responses SHALL include structured start prerequisite and start eligibility data so clients do not parse issue body text.

#### Scenario: 获取全局状态
- **WHEN** CLI 请求 `GET /api/status`
- **THEN** 返回当前项目的 Issue 状态

#### Scenario: 获取所有项目状态
- **WHEN** CLI 请求 `GET /api/status?all=true`
- **THEN** 返回所有项目的 Issue 状态

#### Scenario: 获取单个 Issue 详情
- **WHEN** CLI 请求 `GET /api/issues/:number`
- **THEN** 返回指定 Issue 的详细信息
- **AND** the response includes `prerequisites` and `startEligibility`

#### Scenario: List Issues includes start eligibility
- **WHEN** a client requests `GET /api/issues`
- **THEN** each Issue item includes `prerequisites` and `startEligibility`
- **AND** if the Issue is waiting for prerequisite delivery, `startEligibility.waitingForDelivery` identifies the prerequisite issue numbers

### Requirement: Start handler 校验 issue status

`POST /api/issues/:number/start` SHALL 在执行前校验 issue status，blocked 的 issue 不允许 start。The same start handler SHALL also enforce start eligibility from issue-level start prerequisites before enqueueing work.

#### Scenario: Start blocked issue
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `blocked`
- **THEN** server 返回 400 错误
- **AND** 错误信息包含 "blocked"

#### Scenario: Start active issue in draft stage
- **WHEN** CLI 请求 `POST /api/issues/:number/start`
- **AND** issue status 为 `active` 且 stage 为 `draft`
- **THEN** 正常启动 agent

#### Scenario: Start issue waiting for prerequisite delivery
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** Issue #201 has prerequisite issue #200
- **AND** Issue #200 is not delivered
- **THEN** server returns a 400-class response
- **AND** the response includes an actionable message equivalent to `Issue #201 is waiting for prerequisite #200 to be delivered.`
- **AND** the response includes structured `startEligibility` data with `waitingForDelivery` identifying Issue #200
- **AND** server SHALL NOT enqueue `start-pipeline`

#### Scenario: Start issue after prerequisites delivered
- **WHEN** a client requests `POST /api/issues/201/start`
- **AND** every prerequisite issue for Issue #201 is delivered
- **AND** Issue #201 otherwise satisfies the existing start checks
- **THEN** server enqueues `start-pipeline`
- **AND** returns the existing accepted start response

## ADDED Requirements

### Requirement: API accepts issue start prerequisite declarations

The HTTP API SHALL provide a structured way to declare that an Issue has a prerequisite issue that must be delivered before start. Declaration requests SHALL identify Issues by structured fields rather than requiring body text parsing.

#### Scenario: Declare start prerequisite
- **WHEN** a client declares that Issue #201 requires Issue #200 before start
- **THEN** the API records Issue #200 as a prerequisite issue for Issue #201
- **AND** the response includes updated `prerequisites` and `startEligibility` for Issue #201

#### Scenario: Reject circular start prerequisite declaration
- **WHEN** declaring a start prerequisite would make an Issue directly or indirectly require itself before start
- **THEN** the API returns a 400-class response
- **AND** the response reason is `circular-prerequisite`
- **AND** the rejected prerequisite is not recorded

### Requirement: API represents start eligibility with prerequisite language

The HTTP API SHALL name issue-level prerequisite response fields using `prerequisites`, `startEligibility`, and `waitingForDelivery`. The API SHALL NOT expose this behavior through a legacy dependency-status or blocked-start response model.

#### Scenario: Response describes a waiting issue
- **WHEN** an Issue is waiting for prerequisite issue #200 to be delivered
- **THEN** the API response includes `startEligibility.startable = false`
- **AND** the API response includes `startEligibility.waitingForDelivery` with Issue #200
- **AND** each prerequisite entry indicates whether its prerequisite issue is delivered
