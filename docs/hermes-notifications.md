# Hermes 通知

Mohist 面向 self-host 的长跑场景：issue 启动后，生产线可能一跑几十分钟甚至几小时，你不会一直守在屏幕前。但生产线上有些时刻必须等你——到达审批点、执行失败、issue 完成。Hermes 通知把这些时刻推送到你的聊天工具（Telegram、微信等），在手机上就能看到，回一条命令就能让生产线继续。

## 心智模型

- **Mohist 决定何时通知、说什么**：它盯着生产线，在关键时刻把消息写好。
- **Hermes 负责送达**：消息交给通知伙伴 Hermes，由它推送到具体聊天平台。Mohist 不直接对接任何聊天平台。
- 通知是**即时推送，不是持久记录**：错过的通知不会补发；要回看完整历史，Web 收件箱才是真源。
- 通知**永不干扰生产线**：推送失败只会被记下后放弃，绝不阻塞或影响 issue 与 workflow 的执行。

## 哪些时刻会通知

五种时刻，四种默认开启：

| 时刻 | 默认 |
|---|---|
| 到达审批点，等你决策 | 开 |
| 工作流失败，issue 阻塞 | 开 |
| issue 完成 | 开 |
| Agent 响应失败（它没能处理本该处理的事） | 开 |
| issue 开始工作 | **关**（多半是你刚亲手启动的，属于噪音） |

每条通知包含：发生了什么、哪个 issue（编号 + 标题）、建议的下一步动作。建议动作总是带着 issue 编号（例如 `approve 42`），在聊天里回话不需要任何上下文。失败通知只给简短原因，不含堆栈。

## 开启

一条向导命令完成 Mohist 侧配置：

```bash
mo notification setup --platform telegram
```

向导会探测本机的 Hermes、生成一个共享密钥、写好 Mohist 的通知配置，并打印出需要在 Hermes 侧执行的订阅命令——照着跑一遍，链路就通了。完整选项见 `mo notification setup --help`。

微信没有默认的接收会话，需要显式指定会话 id（可用 `hermes send --list weixin` 查到）：

```bash
mo notification setup --platform weixin --deliver-chat-id "<你的微信会话 id>"
```

配置写好后重载服务端：

```bash
mo update server
```

最直接的端到端验证：驱动一个真实 issue 走到审批点或完成，看聊天工具里是否收到推送。

## 和故障恢复的衔接

失败通知的建议动作直接对应恢复命令：手机上看到「issue 42 失败」，回到任何一台终端（或让聊天里的 agent 代劳）执行 `mo run retry --issue 42` 即可原地重试。审批通知同理对应 `mo run approve --issue 42 --author "Ada"` / `mo run reject --issue 42 --author "Ada" --message "说明需要修改的内容"`。恢复手段的完整地图见[故障恢复](troubleshooting.md)。

## 微信的推送窗口限制

微信只允许机器人在你**最近主动发过消息之后的一段窗口内**（实践中约 48 小时）向你推送；窗口过期后推送会静默失败。这与价值最高的「issue 完成」通知天然冲突——它往往在你走开很久之后才触发。因此**推荐 Telegram 作为默认通知渠道**，微信当作你正在会话中时的辅助渠道。给机器人随便发一条消息（比如 `hi`），窗口就会重新打开，直到再次过期。

## 实装差距

- 按时刻开关通知（例如打开默认关闭的「issue 开始工作」）还没有命令面：通知相关配置目前需重跑向导或手工编辑服务端配置，统一命令面已列为后续事项。
- 「Agent 响应失败」时刻尚未实装，随 Agent 事件响应设计推进。

---

对应源码：`packages/server/src/Mohist.Server/Notifications/`；CLI `packages/cli/Mohist.Cli/MohistCliCommands.Notify.cs`。协议、配置键与 Hermes 侧接线细节见 [`design/hermes-webhook.md`](../design/hermes-webhook.md)。
