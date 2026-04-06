## Context

`AgentRunnerService.resume()` 已经支持注入任意消息——它的 `message` 参数是 string 类型，会被 append 到 session 中。当前只有 `POST /approve` 调用它，传入固定的 `"[System] User approved. Continue to next stage."`。

这意味着后端的消息注入机制已经就位，只需要：
1. 暴露一个通用的消息注入 API
2. 让 mo attach 的 stdin 能调用这个 API
3. 在 Web UI 添加消息输入界面

关键约束：消息只能在 agent 暂停时注入（`hasPausedSession()` 为 true）。agent 正在运行时注入的消息会被拒绝。

## Goals / Non-Goals

**Goals:**

- 暴露通用消息注入 API
- mo attach 支持交互式 stdin 输入
- Web UI 支持发送自由文本消息
- 注入消息后 agent 自动 resume

**Non-Goals:**

- 不支持 agent 运行时注入消息（需要改 agent loop，风险高）
- 不做消息历史查询 API（comments 已经承担了这个角色）
- 不改 agent loop 的 step 间消息检查

## Decisions

### D1: 复用 AgentRunnerService.resume()

消息注入直接调用已有的 `AgentRunnerService.resume(issue, projectId, ..., message)`。不需要新的 service 方法。

API handler 只需要：
1. 检查 `hasPausedSession(issueNumber)`
2. 调用 `resume()` 并传入用户消息
3. 返回成功

### D2: API 端点设计

```
POST /api/issues/:number/messages
Body: { message: string }
```

返回 200（成功）或 409（agent 未暂停，无法注入）或 404（issue 不存在）。

**替代方案**：复用 `POST /approve` 端点，增加 `message` 参数。不采用——approve 有特定语义（固定消息 + 自动 advance），message injection 是通用能力，应独立。

### D3: mo attach 交互模式

当 mo attach 检测到 `agent_paused` 事件时，启用 stdin 输入提示：
```
[12:38:42] || agent paused            issue #3 (approval needed at build)
> _
```

用户输入文本后：
1. 调用 `POST /api/issues/:number/messages`
2. 显示发送确认
3. 继续监听事件

不使用 `--interactive` flag——当 agent 暂停时自动启用输入，运行时 stdin 被忽略。

### D4: Web UI 消息输入区域

在 IssueDetailPage 的审批面板旁边添加一个 "Send Message" 输入区域，仅在 agent 暂停时显示。

输入消息后调用 `POST /api/issues/:number/messages`，成功后清空输入框并刷新 issue 状态。

### D5: 注入消息与 gate 审批的关系

消息注入和 gate 审批是两种并行的交互方式：
- **Approve**：固定语义，自动 append 预定义消息并 resume
- **Message**：自由文本，用户写什么就 append 什么

两者都调用 `resume()`，可以共存。用户可以 approve，也可以发 "approve but check the error handling first"。

如果用户发了消息但没有 approve，agent 收到的是自由文本，它会根据 LLM 判断决定下一步（可能 advance stage，也可能不 advance）。这给了用户更大的控制力。

## Risks / Trade-offs

- **[Risk] 用户发送无关消息干扰 agent** → 这是用户自己的选择。agent 的 LLM 会根据消息内容决策
- **[Risk] mo attach 交互模式的终端渲染** → SSE 输出和 stdin 提示可能交错。使用 readline 的 prompt 功能，SSE 输出时先清除当前 prompt 行再输出事件，再重新显示 prompt
- **[Low] 后端已有能力** → resume() 已存在，只是暴露 API，风险极低
- **[Depends on] ask-user** → 如果 agent 因为 ask_user 暂停，message injection 也可以用来回复（调用 resolve）。但这需要 ask-user change 先完成
