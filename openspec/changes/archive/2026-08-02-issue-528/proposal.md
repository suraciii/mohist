## Why

Slack 这条链路允许拥堵、发送失败和断线，产品不要求它永不出错——但要求它别骗人。当前
入站 inbox / 出站 outbox 的去重、容量翻转与 Delivery uncertain 状态机已在 store 层落地，
但「诚实契约」并未端到端成立：背压只会翻向 Degraded，积压消退后无人把它翻回 Healthy，
Connection 永久卡死直到人工重建；`mohist-slack` 长时间离线可能超过 Slack 的事件保留窗口，
恢复后系统既不检测也不提示可能漏消息；而 `ConnectionDiagnostic` 没有 Backpressured 分支，
一个已被背压的 Connection 在 Web/CLI 里会被报成 Healthy，Delivery uncertain 与拒绝原因也无
可靠的用户可见出口。结果是用户分不清「没被接受」和「被接受后丢了」，恰恰是本要消除的欺骗。

## What Changes

- **背压可逆。** 新增恢复扫描：当某 Connection 的 inbox 待派发数与 outbox 待发送数都回落到
  容量阈值之下时，把 `ConnectionHealth` 从 Degraded 翻回 Healthy 并清空 Backpressured 原因，
  无需用户重建 Connection。已接受输入与终态投递在翻转前后都不被丢弃。
- **Backpressured 成为一类独立诊断事实。** `ConnectionDiagnostic` 增加 Backpressured 分支，
  区分于用户选择的 Disabled 与凭据/服务失败；汇总区输出可行动原因（inbox 还是 outbox 溢出）
  与唯一下一步（等待排空 / 稍后重试）。Degraded(Backpressured) 不再被静默报成 Healthy。
- **入站拒绝对 Slack 用户可见。** 背压时的入站拒绝不再只是一个 adapter 收到的 HTTP 错误；
  它产出一条明确的可发送回复（或等价的可呈现原因），让发起者看出「这次没被接受，请稍后重试」，
  与「已被接受但结果还没回」严格区分。
- **Delivery uncertain 与拒绝原因对用户可见。** Connection 页、CLI 与可用 Owner 诊断暴露
  处于 Delivery uncertain 的投递及其原因，以及背压拒绝原因；人工重发前明确警告可能重复。
- **投递状态不改执行结果。** 固化为契约：Slack 投递失败/未知不改变 AgentJob 或 AgentTurn
  的权威结果，执行结果裁判只有 Server。明确失败可安全重试（Slack 未接受故不产生重复）；
  发送结果未知走 Delivery uncertain，不盲目重发。
- **长时间离线后的缺口诚实。** 检测 `mohist-slack` 离线时长是否可能超过 Slack 事件保留窗口；
  恢复后明确提示「可能遗漏保留窗口外的消息」，并要求用户重发关键委托，而不是假装补齐。
  不用自动重放代替用户重发。

## Capabilities

- `slack-capacity-backpressure`: 入站 inbox 与出站 outbox 的有界容量契约——已接受输入与终态
  投递在容量压力下不被丢弃；可替代中间进度合并为最新状态；达边界时翻为
  Degraded(Backpressured) 并拒绝新输入；**积压消退后自行恢复接受输入**。
- `slack-delivery-outcomes`: 出站投递结果契约——明确失败可安全重试且不产生重复消息；发送
  结果未知显示 Delivery uncertain 而非盲目重发；投递状态不改变 AgentJob/AgentTurn 权威结果；
  Delivery uncertain、拒绝原因与重发重复警告对用户可见。
- `slack-offline-gap-notice`: Slack 服务长时间离线可能超过平台事件保留窗口；恢复后检测并
  明确提示可能存在缺口，要求用户重发关键委托，不承诺自动补回所有消息。

## Impact

- **Server (`packages/server`):**
  - `Infrastructure/Slack/SlackConnectionHealthBackpressurer.cs` — 新增「翻回 Healthy」能力
    （当前只能翻向 Degraded）。
  - `Infrastructure/Slack/SlackOutboxDispatcherService.cs` / `SlackOutboxDispatcherGrain.cs` —
    在现有三个扫描之外增加背压恢复扫描（按 Connection 聚合 inbox/outbox 待处理计数与容量阈值）。
  - `Agent/Services/ConnectionDiagnostic.cs` — 增加 Backpressured 分支与可行动原因/下一步；
    当前 Backpressured 会落穿到 Healthy。
  - `Api/SlackConnectionRoutes.cs` — 背压入站拒绝产出用户可见回复（非仅 HTTP 409）；视图层
    暴露 Delivery uncertain 投递、拒绝原因与重发重复警告。
  - `Slack/SlackSetupVerifier.cs` 及心跳/服务可用性路径 — 离线时长相对事件保留窗口的缺口检测
    与恢复后提示。
- **`mohist-slack` (`packages/mohist-slack`):** 若入站拒绝需在 Slack 侧呈现明确回复，adapter
  的 ingress 结果处理需对应调整（取决于服务端是返回可发送回复还是独立 delivery）。
- **Web / CLI:** Connection 视图渲染 Backpressured 状态、Delivery uncertain 列表、拒绝原因与
  缺口提示；诊断状态枚举新增 Backpressured。
- **Design docs (`design/`):** `design/slack-agent-connection.md` 的「可靠性契约」与「实装差距」
  小节收敛——背压可逆、缺口提示与诊断状态从「目标」转为「已实装」。
- **Tests:** spec 覆盖背压恢复（回落阈值后翻 Healthy 且不丢已接受/终态行）、Backpressured
  诊断分支、入站拒绝的用户可见性、Delivery uncertain 呈现、离线超保留窗口的缺口提示；
  均走 fake 时间与 fake store，无真实外部依赖。
