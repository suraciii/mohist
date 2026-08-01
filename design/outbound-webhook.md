---
status: wip
---

# 出站 Webhook

出站 webhook 是 Project 内的事件订阅：一条匹配表达式 + 一个投递目标。CloudEvent
发生时，每个命中的订阅把该事件按 CloudEvents 规范渲染成 JSON，HTTP POST 发向自己的
目标 URL，并用共享 secret 签名。

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
| `Match` | 匹配表达式，语法与求值复用 [`event-protocol.md`](event-protocol.md) 的 CEL 子集，不在本文重复 |
| `TargetUrl` | 投递目标，仅允许 `http` / `https` |
| `Status` | `Active`（投递）/ `Disabled`（暂停）/ `Archived`（移除） |
| `CreatedAt` / `UpdatedAt` | 存储时间戳 |

Secret 不是 WebhookSubscription 的字段。它存 [`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)，
kind 为 `WebhookSecret`，地址为 `SecretStoreAddress(projectId, subscriptionId)`。
明文只活在进程内，不进订阅表、不进日志、不进 transcript——与现有连接凭据同一契约。

必须一直成立：

- `Match` 必须通过 [`EventMatchExpression`](../packages/server/src/Mohist.Server/Infrastructure/Events/Matching/EventMatchExpression.cs)
  编译；非法表达式在写入订阅时被拒绝，不进入投递路径。
- `TargetUrl` 必须是合法 `http` / `https` URL；非法在写入时被拒绝。
- secret 经 `ISecretStore` 加密落库；读取面用 `ISecretStore.Redact` 脱敏。

Web 与 CLI 配置的是同一个 WebhookSubscription，不建立两份本地配置。

## Semantics

### 订阅匹配

事件发生时，加载该 Project 内 `Status = Active` 的订阅，逐条用 `Match` 求值；命中的
加入本次投递集合。

- 所有命中订阅各自投递（fan-out）。无 first-match、无排序——那是路由表「只能一个
  决策者」的审批语义，出站投递没有。因此订阅表不带 `Position`。
- 订阅表在事件发生时实时加载，配置实时生效；无启动期固定。
- 求值输入复用 `CloudEventEventMatchInput`，与路由表同一匹配语义。

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
body   = 渲染后的 JSON（UTF-8 字节）
header = "X-Hub-Signature-256: sha256=" + HMAC_SHA256(secret, body)
POST TargetUrl
  Content-Type: application/json
  X-Hub-Signature-256: sha256=<hex>   # 仅在 secret 非空时
```

- secret 为空则不加签名头，投递仍发生。
- 签名用 HMAC-SHA256（GitHub 风格 `X-Hub-Signature-256`），与 Hermes 出站同一实现
  模式，不引入第二种签名模型。

### 失败行为

出站 HTTP POST 为 best-effort：失败（非 2xx、连接拒绝、超时）记日志并落一条持久失败
记录，不重试，不阻塞事件流，不影响 issue / workflow 的执行。失败记录携带订阅、目标、
事件 id 与错误摘要，供 Web / CLI 核对——投递不确定可见，不静默。

## Examples

创建订阅：

```bash
mo webhook subscription create my-release-hook \
  --match 'event.type == "com.mohist.issue.completed"' \
  --target-url 'https://ci.internal/mohist' \
  --secret '<shared-secret>'
```

```text
mo webhook subscription list
mo webhook subscription view <id>
mo webhook subscription edit <id> --match '...' --target-url '...'
mo webhook subscription disable <id>
mo webhook subscription enable <id>
mo webhook subscription rotate-secret <id>
mo webhook subscription delete <id> --yes
```

一次 `issue.completed` 事件命中后，接收方收到的请求：

```http
POST /mohist HTTP/1.1
Host: ci.internal
Content-Type: application/json
X-Hub-Signature-256: sha256=<hex-hmac>

{"specversion":"1.0","id":"evt_123","type":"com.mohist.issue.completed","issue":"42",...}
```

接收方按 CloudEvents 规范解析：`type` / `source` 定事件种类，`issue` / `epic` /
`workflowrunid` 定业务位置，`data` 取事件负载。Mohist 不感知接收方怎么用它。

## Status

未实装。全新能力，无既有代码。

开放问题：

- **可靠性取舍**。本文取 best-effort + 失败可见（与 Hermes 出站同一契约）。对丢失敏感
  的关键投递，应升级为 outbox + 重试（复用 [`slack-agent-connection.md`](slack-agent-connection.md)
  的 claim/ack、重试预算与 dead-letter 骨架）。判断标准：接收方动作失败成本是否高于
  人工重发。第一版先在 Web / CLI 暴露失败记录，按真实丢失率决定是否引入 outbox。
对应源码（实装后）：订阅 store / handler / client 位于
`packages/server/src/Mohist.Server/`（与 `Notifications/`、`Infrastructure/Slack/` 同级）；
CLI 命令面在 `packages/cli/Mohist.Cli/`。
