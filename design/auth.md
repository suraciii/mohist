# 认证与身份

Mohist 控制面的认证与归因模型：每个访问都归属一个 Principal，凭 Credential 证明身份，
Scope 决定能做什么，认证主体落到审批与活动的 actor 上。

边界：只覆盖 Mohist 自有 API 与 SignalR hub 的认证、scope 判定与归因。身份先于权限——
本模型的第一性是每个请求归属一个 Principal；scope 判定是叠加其上的第二阶段能力（见
Status 的落地顺序）。GitHub 等外部平台
的身份与凭据签发见 [`github-integration.md`](github-integration.md)；Slack 成员侧的访问
策略仍归 [`slack.md`](slack.md)；直接外部 Agent caller 的认证、公开 projection 与恢复边界归
[`agent-api.md`](agent-api.md)。
不建多用户、角色、权限组、第三方应用注册与企业身份联邦。

## Model

### Principal

Server 级资源（不 Project-scoped）。三类：

| Kind | 数量 | 来源 | 能力上限 |
|---|---|---|---|
| `admin` | 恒为 1 | bootstrap 建立 | `operator` |
| `service` | 按内置组件 | bootstrap 建立（本机服务进程，如 Slack adapter） | `operator` |
| `agent` | 按 Mohist Agent 定义 | Agent 创建时建立 | 不签发凭据，仅作归因锚点 |

字段：`Id`、`Kind`、`Name`、`CreatedAt`。

必须一直成立：admin 恰有一个；不存在创建或删除 Principal 的 API；agent Principal 不随
Agent 归档而删除——历史归因记录永久指向它。

### Credential

Principal 的凭据。库中只存 token 的 SHA-256 哈希（高熵随机值，无需加盐）。

字段：`Id`、`PrincipalId`、`Kind`、`TokenHash`、`Scopes`、`Name`、`ExpiresAt`、
`RevokedAt`、`CreatedAt`。直接外部 Agent API 使用的 PAT 还持久化一个最小
`ProjectGrant`：`operator_all` 或 `explicit`，后者带非空的 `AllowedProjectIds`。这不是
通用角色、membership 或 ACL 模型；它只回答一把 PAT 能否访问一个私有 Project。

| Kind | 用途 | 承载 |
|---|---|---|
| `session` | Web 与 CLI 登录会话 | cookie / CLI 本地存储 |
| `refresh` | CLI 会话续期 | CLI 本地存储 |
| `pat` | admin 签发的自用令牌（脚本、CI、外部 Agent） | `MOHIST_TOKEN` env |
| `runner` | Runner 机器凭据，绑定 `RunnerId` | runner 本地文件 |
| `integration` | 入站集成令牌，带 ProjectId 约束 | 集成方配置 |

token 形态：`moh_<kind>_<base64url(32B)>`。kind 前缀供人眼识别与泄漏扫描。

**文件型凭据**（不入库）：admin bootstrap 与 service 凭据沿用 `OperatorCredential` 的文件
机制——缺失时自动生成 32 字节随机值，0600、拒符号链接，启动时加载入内存比对。它们是
部署级根凭据；入库会产生「谁初始化库」的循环依赖。文件即凭据，吊销即换文件内容。

**Scope**（封闭集合）：

| Scope | 满足 |
|---|---|
| `operator` | 一切路由 |
| `readonly` | 仅 GET |
| `runner` | `/api/runner/**`、工件与日志上报、`/hubs/runner` |
| `webhook` | 入站集成端点，限 Credential 约束的 Project |

Credential 的 Scopes 不得超过其 Principal 的能力上限；`operator` 满足一切 scope。

### ExternalAgentCaller

直接外部 Agent API 的 Bearer PAT 成功认证后，Server 解析出一个只用于该边界的
`ExternalAgentCaller`：

```text
ExternalAgentCaller {
  callerKeyId = CredentialId
  principalId
  scopes
  projectGrant = operator_all | explicit
  allowedProjectIds = [ProjectId]     # only when projectGrant=explicit
}
```

`callerKeyId` 是 PAT 的稳定内部身份，不是 token 明文，也不是调用方可提交或读取的字段。
`operator_all` 是显式授予私有 Project owner 的所有当前 Project 的 grant；它不是仅凭
`operator` scope 隐式推导。`explicit` 只允许列出的 ProjectId，空列表等价于无 grant 并拒绝。
PAT 签发时必须选择并持久化其中之一；没有 grant 的旧 PAT 不能调用直接外部 Agent API，
直到明确补齐 grant。这样不需要引入多用户 RBAC，却让 Project authorization 有唯一权威。

敏感基础设施面不随 GET 放开给 `readonly`：`/api/fs/**`、`/api/logs/tail`、
`/api/config/**`、`/api/system/**` 与 dead-letter 路由一律声明 `operator`；`readonly`
只覆盖业务资源的观察面（含 `/hubs/events` 与 `/otel/api/**` 查询）。dead-letter 另保留
现状约束：仅在 loopback-only listener 时挂载。

### EnrollmentToken

一次性注册令牌：`TokenHash`、`ExpiresAt`（签发后 15 分钟）、`ConsumedAt`。不预绑定
RunnerId——谁消费，谁登记自己的 RunnerId。

## Semantics

### 认证解析

按以下顺序取第一个命中：

1. `Authorization: Bearer <token>`；
2. `mohist_session` cookie（Web 同源，浏览器 WebSocket 自动携带）。

不允许 query string 携带 token（RFC 6750 §2.3 与 RFC 9700：URI 会进访问日志、浏览器
历史与代理记录）。两个 SignalR hub 因此没有例外通道：Web 走同源 cookie，Runner 的
SignalR client 走 header。

```text
token 与文件型凭据逐一 FixedTimeEquals          -> admin / service Principal
否则 SHA-256(token) 查 Credential               -> 校验 RevokedAt、ExpiresAt -> Principal + Scopes
PAT on direct Agent API                            -> ExternalAgentCaller + ProjectGrant
均失败 -> 401 + WWW-Authenticate: Bearer error="invalid_token"（RFC 6750 §3）；
         对外不区分「不存在 / 过期 / 吊销」
```

豁免清单（此外全部要求认证 + scope）：`/api/health`；登录与设备授权端点；Web 静态资源；
GitHub ingress（自有 HMAC 验签，见 [`github-integration.md`](github-integration.md)）；
OTLP listener 上的 `/otel/v1/*`（端口隔离已有边界）。

Scope 判定：路由声明所需 scope；`readonly` 仅满足 GET；其余按上表。scope 不足返回 403。

### 直接外部 Agent 调用

[`agent-api.md`](agent-api.md) 的 `/api/v1` 直接调用者必须使用 `Authorization: Bearer <PAT>`。
它不是 Web cookie，也不是 Slack 等 Agent Connection 的受信任服务身份；Connection 仍在自己的
adapter 边界内处理外部平台身份，不能冒充直接 caller。

该边界的 route scope 固定为：launch、follow-up、stop 要 `operator`；Input、Turn 与 Session
公开事件读取要 `readonly` 或 `operator`。认证后的 PAT 必须先解析为上述
`ExternalAgentCaller`，再同时验证 route scope 与 route `projectId` 是否匹配其显式 grant 或
`operator_all` grant。选中的 Project 不在 grant 中一律 `403 forbidden`，即使该 Project
不存在也不改为 `404`；只有 grant 已通过后，缺失的 Project/resource 才按资源语义返回 `404`。
这不新增跨用户 visibility、角色或加密策略。

执行顺序固定为认证 Bearer PAT、解析 ExternalAgentCaller、授权 scope 和 Project grant、验证
Project/resource 归属、校验请求、再做 idempotency lookup/fingerprint/admission。`401` 或 `403`
时不得读取或返回已有 request mapping，也不得写 rejection、Job、Session、Input、Turn、outbox
或公开事件，亦不得调用 Runner。完整的外部字段、错误和 cursor 语义只在
[`agent-api.md`](agent-api.md) 定义，不能从本认证文档或 Connection 边界推导另一套 API。

### Bootstrap

```text
~/.mohist/admin-token    不存在 -> 生成写入（0600，拒 symlink）   # admin Principal
~/.mohist/operator-token 不存在 -> 同上                            # service Principal，沿用现机制
启动时加载两者为文件型凭据
```

`X-Mohist-Operator-Token` header 退役，全部统一 `Authorization: Bearer`。Slack adapter 改持
service 凭据走 Bearer；原 operator token 文件内容即 service 凭据，已部署环境无需轮换。

### Web 登录

`POST /api/auth/session {token}`：校验为 `operator` 凭据后签发 `Credential(kind=session,
7 天)`，写 `Set-Cookie: mohist_session=<token>; HttpOnly; SameSite=Lax; Path=/`（https
请求附加 `Secure`）。SameSite=Lax 加 JSON API 已使跨站表单无法携带会话，不另建 CSRF
token。登出吊销该 Credential。SPA 遇 401 呈现登录页。不设密码——粘贴令牌即登录。

### CLI 设备授权（RFC 8628）

豁免端点：`POST /api/auth/device/code`、`POST /api/auth/token`。确认页 `/device` 要求已
登录的 Web 会话。两个端点限流：轮询与猜码超过每来源每分钟数次即 `slow_down` / 429。

```text
CLI  POST device/code {name}  -> {device_code, user_code, verification_uri,
                                   verification_uri_complete, interval=5, expires_in=600}
用户 打开 verification_uri(_complete) -> 输入 user_code -> approve -> 记录批准（admin Principal）
CLI  轮询 POST token {grant_type=urn:ietf:params:oauth:grant-type:device_code, device_code}
     <- authorization_pending / slow_down / expired_token
     <- 成功 {access_token(kind=session, 1h), refresh_token(kind=refresh, 30d)}
```

`user_code`：8 字符，字母表 `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`（去 I/O/0/1，同 Slack
认领码先例）；CLI 以 `XXXX-XXXX` 分组显示，确认页输入忽略连字符与大小写。

续期：`POST token {grant_type=refresh_token}`——旧 refresh 立即作废并保留其哈希至窗口
结束，签发新 access + refresh（滚动轮换）。再次出示已作废 refresh 视为泄露：吊销该
设备授权派生的整条会话链（RFC 9700 §4.14.2 的 family 撤销）。CLI 会话存
`~/.mohist/credentials.json`（0600）。

CLI 凭据解析顺序：`MOHIST_TOKEN` env > credentials.json（按 server 匹配）> admin-token
文件（本机）。401 先尝试续期，失败提示 `mo auth login`。

### PAT

`mo auth token create --name <n> --scope operator|readonly [--ttl <hours>]`：签发
`Credential(kind=pat)`，完整值仅响应一次。PAT 必须有过期：`--ttl` 缺省 90 天、上限
1 年（GitHub fine-grained PAT 同此纪律），不允许永不过期。`--name` 在同一 Principal 的
活跃凭据中唯一。`revoke` 置 `RevokedAt`；`list` 只显示名称与前缀（`moh_pat_…`），不
显示完整值。集成令牌（`kind=integration`）不由本命令签发，其签发入口由对应入站
集成的 spec 定义。

要用于直接外部 Agent API 的 PAT，签发时还必须持久化一个 `ExternalAgentCaller`
`ProjectGrant`：明确的 `operator_all`，或非空的 `AllowedProjectIds`。没有这项选择的
既有 PAT 仍可用于其原有控制面，但直接外部 Agent 路由一律拒绝；不以“当前 Project”或
Principal 的猜测补齐 grant。

### Runner 注册与凭据

```text
mo install runner（admin 已认证）: POST /api/auth/runner-enrollments -> EnrollmentToken
安装器把令牌注入 runner 环境
runner 首启: POST /api/auth/runner/credentials {enrollment_token, runner_id, hostname}
             校验未消费未过期 -> 消费 -> 签发 Credential(kind=runner, 绑定 runner_id)
             -> runner 存 $RUNNER_ROOT/credential（0600）
之后: Bearer 访问 /api/runner/** 与 /hubs/runner
```

路径与 hub query 里的 `runnerId` 必须与凭据绑定的 `RunnerId` 一致，否则 403——顶替防护
在认证层完成，不再信任自声明。吊销后该 runner 全部请求 401；恢复即重新走注册流程。

### 归因

认证通过后，mutating handler 把 Principal 记入领域动作的 actor：审批 `decidedBy`、评论
作者、活动记录。`--author` 保留为展示别名，不再充当归属依据。Agent 归因沿用执行协议
已有的 job/agent 身份上报，agent Principal 是这些记录指向的稳定锚点。

### 审计事件

落持久记录（不含 token 明文）：凭据签发 / 吊销 / 消费、EnrollmentToken 签发与消费、
设备授权批准、session 建立。

## Examples

本机 CLI：

```text
server 首启 -> admin-token 生成
mo issue list -> 命中 admin-token 文件 -> admin Principal，operator
```

远程 CI：

```text
mo auth token create --name ci --scope readonly --ttl 720h -> moh_pat_...（显示一次）
MOHIST_TOKEN=moh_pat_... mo issue list    -> readonly 满足 GET -> 200
MOHIST_TOKEN=moh_pat_... mo issue create  -> scope 不足 -> 403
```

Runner 顶替防护：

```text
凭据绑定 runner-a；携 runner-a 凭据 POST /api/runner/runner-b/heartbeat -> 403
```

## Status

全部未实装。当前仅 Slack adapter / Manager ingress / dead-letter 三面由
`OperatorCredential` 手工校验，主 API 与两个 SignalR hub 无认证。

落地顺序（供 `mohist-explore` 切分）——**身份认证先行，权限检查后到**：

1. P0 身份认证：Principal / Credential、认证解析与豁免清单、bootstrap、Web 登录、
   CLI 设备授权与 PAT、`X-Mohist-Operator-Token` 退役、actor 归因。此阶段不做 scope
   判定：任何有效凭据可达任何需认证路由。相对现状（全裸奔）已把「陌生人」挡在门外；
   凭据滥用的收敛留给 P2。PAT 签发时仍记录 Scopes（数据模型就位），P2 起生效。
2. P1 机器身份：Runner enrollment 与凭据签发、integration 令牌落地；凭据绑定信息
   （RunnerId、ProjectId）照常记录，路由 gate 同样留给 P2。
3. P2 权限检查：scope 判定与 403、敏感基础设施面归属、runner 顶替防护、审计事件，以及
   PAT 的 `ExternalAgentCaller` Project grant 解析与先授权后 idempotency/admission。
4. P3（候选，另立设计）：外部 OIDC 与多用户。

`#387` 的直接外部 Agent API 不能在只完成 P0 的阶段发布；它要求上述 PAT 认证和 P2 的
scope/Project grant 授权一起生效，才能满足认证、授权先于 idempotency 与 admission 的边界。

开放问题：

- session 续期策略（7 天绝对过期还是滑动窗口）实装时定，不影响模型。
