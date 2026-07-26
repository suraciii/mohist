# Hermes Webhook

Mohist 通过出站 webhook 把 issue 工作流的关键事件推给 Hermes，由 Hermes 完成聊天平台（Telegram、微信等）的实际投递。产品语义（解决什么问题、哪些时刻通知、怎么开启）见 [`docs/hermes-notifications.md`](../docs/hermes-notifications.md)，本篇承载协议与配置细节。

分工是刻意的：Mohist server 是状态与裁决平面，从不派生进程。它只做两件事：渲染消息体，HTTP POST 给 Hermes。聊天侧的一切由 Hermes 承担。

## 事件类型

通知时刻的产品语义与默认开关见
[`docs/hermes-notifications.md`](../docs/hermes-notifications.md)。wire 上的
`notificationType` 与触发事件对应：

| notificationType | 触发事件 |
|---|---|
| `approval_requested` | `workflow.stage.approval-requested` |
| `workflow_failed` | `workflow.run.failed` |
| `issue_completed` | `issue.completed` |
| `issue_started` | `issue.work-started` |

第五个时刻「Agent 响应失败」尚未实装，wire 形状随 Agent 事件响应落地。

## Mohist 配置

出站 webhook 配置在 `~/.mohist/config.jsonc`：

```jsonc
{
  "Mohist": {
    "Notifications": {
      "Hermes": {
        "WebhookUrl": "http://127.0.0.1:8644/webhooks/mohist",
        "Secret": "the-shared-secret",
        "EnabledTypes": [
          "approval_requested",
          "workflow_failed",
          "issue_completed"
        ]
      }
    }
  }
}
```

- `WebhookUrl` — Hermes webhook 接收端 URL。缺省或为空时 Mohist 不发送任何东西。
- `Secret` — 可选。设置后 Mohist 在 JSON body 上计算 HMAC，加 `X-Hub-Signature-256: sha256=<hex-hmac>` 头（GitHub 风格，Hermes 按此校验）。必须与 Hermes 侧的**订阅级** secret 一致（见下文「两层 secret」）。
- `EnabledTypes` — 发送哪些时刻。需要启动提醒时加入 `issue_started`。

改完配置后重载受管服务：

```bash
mo update server
```

> 注意：通知配置是嵌套 section，这一节需直接编辑 `~/.mohist/config.jsonc`（统一配置命令面已列为 follow-up）。

首份配置可用向导生成：

```bash
mo notification setup --platform telegram
# 平台没有默认 home channel 时（如 weixin）：
mo notification setup --platform weixin --deliver-chat-id "<your-weixin-chat-id>"
```

向导探测 Hermes webhook 端口、生成共享 secret、写入上面的 Mohist section，并打印配对的 `hermes webhook subscribe` 命令（提供了 chat id 时折进命令里）。flags 见 `mo notification setup --help`。

## Hermes 侧接线

以下都是 Hermes 自己的命令与配置，Mohist 不修改 Hermes 配置。

### 1. 启用 Hermes 的 webhook 平台

在 `~/.hermes/config.yaml` 加**顶层** `platforms.webhook` 块（必须顶层，不能放在 `gateway.platforms` 下）：

```yaml
platforms:
  webhook:
    enabled: true
    extra:
      host: "127.0.0.1"   # Mohist 与 Hermes 同机
      port: 8644
      secret: "<any-strong-random-string>"   # 平台级，见下方两层 secret 说明
```

重启 gateway 并验证监听：

```bash
hermes gateway restart
curl http://127.0.0.1:8644/health
# {"status": "ok", "platform": "webhook"}
```

> **两层 secret，不要混淆。** 上面的 `platforms.webhook.extra.secret` 是**平台级**；下一步创建的订阅有自己的**订阅级** secret（传给 `hermes webhook subscribe` 的 `--secret`）。Hermes 校验入站 POST 用的是**订阅级** secret，Mohist 配置里的 `Secret` 必须与订阅级一致，而不是平台级。

### 2. 创建 mohist 订阅

用订阅级 secret（与 Mohist `Secret` 同值）。`--prompt` 模板保持最简——消息体已由 Mohist 渲染好，Hermes 只做透传：

```bash
hermes webhook subscribe mohist \
  --deliver telegram \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

- `--deliver-only` — 跳过 agent loop，把渲染后的模板原样投递，零 LLM 开销。
- `--prompt '{body}'` — Hermes 模板语法是单花括号 `{field}` 占位符，引用 POST body 的字段。Mohist 把完整消息渲染进 `body`，裸 `{body}` 就够了。（其余可用字段：`{issueNumber}`、`{issueTitle}`、`{notificationType}`、`{stage}`、`{suggestedAction}` 等——除非要自定义排版，否则优先用 `{body}`。）
- `--deliver <platform>` — 推送到哪个聊天平台。

### 3. 需要 chat id 的平台

Telegram 有默认 home channel，`--deliver telegram` 即可。**微信（weixin）没有**，必须显式给 chat id：

```bash
hermes webhook subscribe mohist \
  --deliver weixin \
  --deliver-chat-id "<your-weixin-chat-id>" \
  --deliver-only \
  --secret "<same-secret-as-Mohist>" \
  --prompt '{body}'
```

chat id 用这条查：

```bash
hermes send --list weixin
```

### 4. 验证

发一个带签名的测试 POST，模拟 Mohist 的出站 payload：

```bash
curl -X POST http://127.0.0.1:8644/webhooks/mohist \
  -H "Content-Type: application/json" \
  -H "X-Hub-Signature-256: sha256=<hmac>" \
  -d '{"body":"Mohist notification link verified.","issueNumber":0}'
```

返回 `{"status":"delivered"}` 说明链路全通。（手算 HMAC 很麻烦，最省事的端到端验证是驱动真实 issue 走到审批点/完成，看聊天工具收没收到。）

## Payload

Mohist 向 `WebhookUrl` POST JSON（camelCase，遵循 CloudEvents/web 惯例）：

```json
{
  "notificationType": "approval_requested",
  "eventType": "com.mohist.workflow.stage.approval-requested",
  "sourceEventId": "evt_123",
  "occurredAt": "2026-07-03T12:01:00+00:00",
  "projectId": "proj_123",
  "issueNumber": 42,
  "epicNumber": 7,
  "issueTitle": "Add login rate limiting",
  "workflowRunId": "wr_123",
  "stage": "plan",
  "failureReason": null,
  "suggestedAction": "approve 42",
  "body": "Issue #42 在 plan 阶段等待审批决策。下一步：approve 42"
}
```

- `body` — 预渲染的消息文本（按通知种类措辞，使用 Mohist 配置的语言）。默认 Hermes `{body}` 模板消费的就是它；其余字段供自定义模板或未来渠道使用。
- `epicNumber` — Issue 当前无 Epic 归属时省略。
- `failureReason` — 只在 `workflow_failed` 时有值，只带简短原因，永不携带堆栈。
- `suggestedAction` — 总是携带 issue 编号。

## 签名校验

`Secret` 设置后，Mohist 对 JSON body 计算 HMAC-SHA256，以 `X-Hub-Signature-256: sha256=<hex-hmac>` 头随请求发送——这是 Hermes 校验的 GitHub 风格签名头。校验用的是**订阅级** secret（`hermes webhook subscribe --secret`），与平台级 secret 无关。

## 微信客服窗口限制

微信（经 iLink）只允许机器人在用户最近一次主动发消息后的有限窗口内推送（实践中约 48h）。窗口过期后出站通知静默失败，返回 `ret=-2`——Hermes 会报成 "rate limited"（有误导性，实际是窗口过期而非限速）。

这与价值最高的「issue 完成」通知冲突：它往往在用户走开很久之后触发。**默认通知渠道优先选 Telegram**，微信作为活跃会话期间的辅助渠道。给机器人发任意消息（如 `hi`）可重新打开窗口。

## 投递可靠性

事件如何送达订阅 handler，以事件总线为真源：见 [`eventbus.md`](eventbus.md)（at-least-once、Polly 重试、DLQ 均是总线层机制）。

Hermes webhook 特有的是最后一跳——出站 HTTP POST——为 best-effort：失败（非 200、连接拒绝等）记日志后吞掉，不重试，永不阻塞或影响 issue / workflow 的执行；这一跳没有自己的 outbox、重试队列或 DLQ。

需要「发生过什么」的持久记录时，以 Web Inbox 为真源；webhook 是瞬时推送，不是持久日志。

## 对应源码

`packages/server/src/Mohist.Server/Notifications/`（options、renderer、payload、`HermesWebhookClient`）；订阅入口 `packages/server/src/Mohist.Server/Events/Subscriptions/HermesIssueNotificationHandler.cs`；CLI 向导 `packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs`。
