---
status: stable
---

# 出站 Webhook

出站 webhook 是 Project 内的事件订阅：选择「哪些事件、发到哪里、按接收方要求怎么认证」。
CloudEvent 发生时，每个命中的订阅把该事件按 CloudEvents 规范渲染成 structured JSON，
`POST` 发向自己的目标 URL。

v1 的产品立场：**Mohist 是通用的 HTTP webhook 生产者，不是某个接收方的适配器。** 接收方
要求什么认证（`Authorization: Bearer`、Basic、自定义 header 如 `X-Webhook-Secret`），就由
订阅配置什么；Mohist 只负责把标准 CloudEvents 1.0 POST 过去。**Mohist 自有的消息签名（HMAC）
不在 v1 范围**，仅为兼容旧订阅保留，新订阅默认不带签名。

出站 webhook 是 Mohist 对外的 OHS + PL：Mohist 以 CloudEvent 为发布语言、以标准
webhook 为开放主机服务，任何下游按 CloudEvents 标准订阅消费，Mohist 不针对某个特定
接收方（buzz、CI 或其他系统）特化。它不是 Agent 响应（那是路由表，
[`event-routing.md`](event-routing.md)），不是聊天通知（那是 Hermes，
[`hermes-webhook.md`](hermes-webhook.md)），也不是 workflow 执行中的 `call_webhook`
action（那是 workflow step 的主动调用）。

出站 webhook 只做 Mohist → 下游的单向投递。下游回流 Mohist（入站、需认证）不在本
spec 范围——那是独立的入站能力，后续单独设计。因此本文不覆盖回流循环防护，也不对
出站目标做安全限制。

## Model

WebhookSubscription 是 Project-scoped 资源。它只声明「哪些事件、发到哪里、用什么
secret 签名」，不持有会话、执行或接收方状态。

| 字段 | 说明 |
|---|---|
| `Id` / `ProjectId` | 身份与所属 Project |
| `Name` | 可读的订阅名，用于 CLI / Web 标识 |
| `TargetUrl` | 投递目标，仅允许 `http` / `https` |
| `EventSelectionMode` | `all`（投递所有事件，默认）或 `selected`（仅 `EventTypes` 列出的） |
| `EventTypes` | 当 `EventSelectionMode = selected` 时生效的事件类型清单（JSON 数组） |
| `AuthType` | 接收端认证：`none` / `bearer` / `basic` / `custom`（默认 `none`） |
| `Match` | 可选的高级 CEL 过滤，在已选事件基础上进一步收窄；为空表示无额外过滤 |
| `Status` | `Active`（投递）/ `Disabled`（暂停）/ `Archived`（移除） |
| `CreatedAt` / `UpdatedAt` | 存储时间戳 |

凭据不进订阅表。v1 认证凭据（bearer token、basic user:pass、或自定义 header 的 JSON map）
存 [`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)，
kind 为 `WebhookSecret`，地址为 `SecretStoreAddress(projectId, "<subscriptionId>:auth")`
——在 connectionId 上加 `:auth` 命名空间，使其与旧式 HMAC 签名 secret（地址
`SecretStoreAddress(projectId, subscriptionId)`）共存，无需新增 SecretKind 或改库表。
明文只活在进程内，不进订阅表、不进日志、不进 transcript——读取面用
`ISecretStore.Redact` 脱敏，任何 API / CLI / 失败记录都看不到值。

必须一直成立：

- `EventSelectionMode = selected` 时 `EventTypes` 不得为空，且每个类型必须存在于
  [`EventCatalog.All`](../packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs)；
  未知类型在写入订阅时被拒绝。
- `Match` 若提供，必须通过 [`EventMatchExpression`](../packages/server/src/Mohist.Server/Infrastructure/Events/Matching/EventMatchExpression.cs)
  编译；非法表达式在写入时被拒绝。`Match` 为空合法（默认无额外过滤）。
- `TargetUrl` 必须是合法 `http` / `https` URL；非法在写入时被拒绝。
- `AuthType` 必须是 `none` / `bearer` / `basic` / `custom` 之一。
- 凭据经 `ISecretStore` 加密落库；读取面用 `ISecretStore.Redact` 脱敏。

Web 与 CLI 配置的是同一个 WebhookSubscription，不建立两份本地配置。

## Semantics

### 订阅匹配

事件发生时，加载该 Project 内 `Status = Active` 的订阅，逐条判定：

1. **事件选择**：`EventSelectionMode = selected` 时，事件的 `type` 必须在 `EventTypes` 中；
   `all` 时通过。
2. **高级 CEL（可选）**：`Match` 非空时，用 [`EventMatchExpression`](../packages/server/src/Mohist.Server/Infrastructure/Events/Matching/EventMatchExpression.cs)
   求值，复用 `CloudEventEventMatchInput`，与路由表同一匹配语义。两者都通过才投递。

- 所有命中订阅各自投递（fan-out）。无 first-match、无排序——那是路由表「只能一个
  决策者」的审批语义，出站投递没有。因此订阅表不带 `Position`。
- 订阅表在事件发生时实时加载，配置实时生效；无启动期固定。

### Payload 渲染

投递体是 CloudEvent 按 CloudEvents structured content mode 序列化的 JSON，渲染无需
模板引擎——CloudEvent 即 PL。extensions 按规范作为顶层自定义属性并入：

```json
{
  "specversion": "1.0",
  "id": "evt_123",
  "source": "mohist://proj_123",
  "type": "com.mohist.issue.completed",
  "time": "2026-08-01T12:01:00+00:00",
  "datacontenttype": "application/json",
  "data": { "issueNumber": 42, "title": "Add login rate limiting" },
  "projectid": "proj_123",
  "issue": "42",
  "epic": "7",
  "workflowrunid": "wr_123"
}
```

`data` 为事件自身负载；`projectid` / `issue` / `epic` / `workflowrunid` 等顶层属性来自
事件业务谱系 extensions（[`CloudEventLineage`](../packages/server/src/Mohist.Server/Infrastructure/Events/CloudEventLineage.cs)），
是下游理解事件业务位置的唯一权威，不二次加工。

### 签名与发送

```text
body   = 渲染后的 CloudEvent JSON（UTF-8 字节）
POST TargetUrl
  Content-Type: application/cloudevents+json
  Authorization: Bearer <token>           # AuthType=bearer
  Authorization: Basic <base64(user:pass)> # AuthType=basic
  <Custom-Header>: <value>                 # AuthType=custom（每条配置的 header）
  X-Hub-Signature-256: sha256=<hex>        # 仅旧式签名 secret 非空时（兼容）
```

- `Content-Type` 为 CloudEvents 1.0 structured JSON 媒体类型 `application/cloudevents+json`。
- 认证 header 由订阅的 `AuthType` 决定：`none` 不加；`bearer` / `basic` 加 `Authorization`；
  `custom` 加用户配置的每个 header。值从 `ISecretStore` 读出后在发送时拼装，不进日志。
  受传输控制或 CloudEvents 保留的 header（`Host`、`Content-Length`、`Transfer-Encoding`、
  `Content-Type`、`X-Hub-Signature-256`、`X-Mohist-*`）禁止被自定义覆盖。
- **签名是兼容能力，不是 v1 路径**：仅当旧订阅存有 HMAC 签名 secret 时才加
  `X-Hub-Signature-256`（GitHub 风格 HMAC-SHA256）。新建订阅默认 `AuthType=none`、无签名。
  v1 不在创建面暴露签名 secret。
- 固定超时 15 秒；`OperationCanceledException` 记为「request timed out」失败。

### 失败行为

出站 HTTP POST 为 best-effort：失败（非 2xx、连接拒绝、DNS/TLS、超时）记日志并落一条
持久失败记录，不重试，不阻塞事件流，不影响 issue / workflow 的执行。失败记录携带订阅、
目标、事件 id、HTTP 响应状态（若有）、耗时与错误摘要，供 Web / CLI 核对——投递不确定
可见，不静默。2xx 视为成功，不落记录。

> 自动重试 / outbox / delivery success 历史 / 手动重投 / 测试投递属于后续 slice，不在本文 v1 范围。

## Examples

创建订阅（事件勾选 + 自定义 header，直连接收方，无需 CEL、无需 bridge）：

```bash
# 列出可选事件类型（分组）
mo webhook event-types

# 订阅 issue 完成，把接收方要求的 X-Webhook-Secret 配成自定义 header
mo webhook subscription create my-ci-hook \
  --event com.mohist.issue.completed \
  --target-url 'https://ci.internal/mohist' \
  --auth-type custom \
  --auth-header 'X-Webhook-Secret=<receiver-secret>'

# 高级：用 CEL 进一步收窄（在已选事件基础上）
mo webhook subscription create my-fine-hook \
  --event com.mohist.issue.completed \
  --match 'event.issue == "42"' \
  --target-url 'https://ci.internal/mohist'
```

```text
mo webhook subscription list
mo webhook subscription view <id>
mo webhook subscription edit <id> --event ... --target-url '...'
mo webhook subscription disable <id>
mo webhook subscription enable <id>
mo webhook subscription delete <id> --yes
mo webhook subscription failures [--subscription-id <id>]
```

一次命中的 `issue.completed` 事件后，接收方收到的请求：

```http
POST /mohist HTTP/1.1
Host: ci.internal
Content-Type: application/cloudevents+json
X-Webhook-Secret: <receiver-secret>

{"specversion":"1.0","id":"evt_123","type":"com.mohist.issue.completed","issue":"42",...}
```

接收方按 CloudEvents 规范解析：`type` / `source` 定事件种类，`issue` / `epic` /
`workflowrunid` 定业务位置，`data` 取事件负载。Mohist 不感知接收方怎么用它。

## Status

v1（本文描述的契约）已实装：通用 HTTP + 可配置 endpoint 认证（none/bearer/basic/custom）
+ 事件勾选（CEL 为高级过滤）+ CloudEvents 媒体类型 + 2xx 成功语义 + 失败记录（含 HTTP
状态与耗时）。迁移 `20260802000000_WebhookV1AuthAndEvents` 为旧订阅回填默认值
（`AuthType=none`、`EventSelectionMode=all`、`EventTypes=[]`），旧 HMAC 签名 secret 保留
可读，行为不静默改变。

明确不在 v1 范围（后续 slice）：测试投递、成功+失败的 delivery attempt 历史、手动重投、
Web 管理界面、自动重试 / outbox、Mohist 自有签名协议与密钥轮换。

对应源码：订阅 store / handler / client 位于
`packages/server/src/Mohist.Server/Webhooks/`；API 路由在
`packages/server/src/Mohist.Server/Api/WebhookSubscriptionsRoutes.cs`；
CLI 命令面在 `packages/cli/Mohist.Cli/MohistCliCommands.Webhook.cs`。
