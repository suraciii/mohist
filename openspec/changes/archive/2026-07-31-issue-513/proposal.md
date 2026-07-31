## Why

用户想在 Web 或 CLI 启动、继续 Agent 时明确附上本地文件，并确认 Agent 真正收到了哪些；文件不可用时产品必须如实告知，不能假装读过。但今天的 Agent 输入只接受文本：`AgentSessionInputRecord` 只有 `Text`，launch / follow-up 的 body 只认 `prompt` / `text`，Runner 只把拼好的字符串交给 Runtime。Web 启动器虽能上传文件并把 `att:<id>` 内联进 prompt 文本，这条链路却是断的——附件从不绑定到任何 Agent 输入所有者、没有 Agent 侧内容读取路由、Runner 也不解析 `att:` 引用，上传后 24h 即被清理。结果正是 issue 要杜绝的：用户以为附了文件，Agent 其实读不到，失败还被静默吞掉。

## What Changes

- 把附件做成 SessionInput 显式拥有的输入资源，而非散落在 prompt 文本里的内联引用。launch 与 follow-up 的输入都可由「文本 + 零或多个附件」组成。
- 允许只有附件、没有文本的有效输入：建立正常的 SessionInput 与 AgentTurn，Mohist 不为纯附件输入暗中编造提示词。
- 输入受理对每个附件给出确定结果（接受 / 不可用 / 超限 / 类型不支持）；提交执行前必须明确列出未被使用的附件，不允许把部分成功包装成全部成功。
- 每个被接受的附件记录用户可见的来源、名称、类型、大小与可用性；其内容只能由所属输入的执行路径读取，另一个 Session、用户或 Connection 不能仅凭引用复用同一附件。
- 删除与到期遵循统一、可解释的保留规则，且不影响已持久化的工作与会话结果。
- Runner 在所属 Turn 内把已接受的附件解析为 Agent 可读的内容，而非把不可打开的 URL 或文本引用丢给 Runtime；解析只走所属输入的访问路径，临时下载地址、调用方凭据和原始平台事件不进入 Instructions、回复或 transcript。
- Web 与 CLI 在启动与 follow-up 都可附加文件、提交前看到待发送清单、并看到每个文件的受理结果；Web 以显式附件归属取代当前断开的内联 `att:` 文本路径。普通 URL 仍是消息文本，是否访问由 Agent 已配置的能力决定。

## Capabilities

- `session-input-attachments`: Agent 输入（launch 与 follow-up）把附件作为一等输入内容接受。输入可纯文本、纯附件或两者并存；纯附件是有效输入，建立正常 SessionInput 与 AgentTurn。受理对每个附件给出确定结果，不可用 / 超限 / 类型不支持的附件在提交执行前被明确列出，不被静默丢弃，也不把部分成功包装成全部成功。被接受的附件只归属于接受它的那条 SessionInput。
- `attachment-input-lifecycle`: 附件成为 Mohist 管理的输入资源——记录用户可见的来源、名称、类型、大小与可用性；上传→绑定到所属输入→内容读取→保留 / 清理走统一边界。其内容只能经所属输入的执行路径读取，另一 Session、用户或 Connection 不能仅凭引用复用；删除与到期遵循可解释的保留规则，不影响已持久化的工作与会话结果。附件资源与观察面不暴露调用方的临时地址、凭据或原始平台事件。
- `agent-attachment-delivery`: 已接受的附件在所属 Turn 内到达 Runtime、成为 Agent 可读的内容。Runner 解析已接受附件为内容而非透传不可打开的 URL 或文本引用，只经所属输入的访问路径取内容；临时下载地址、provider token 与原始事件 payload 不进入 Instructions、回复或 transcript。
- `agent-attachment-entry`: Web 与 CLI 在启动与 follow-up 都能附加文件、提交前预览待发送清单、并读取每个文件的受理结果，对成功与失败项使用同一套解释。Web 以显式附件归属取代当前内联 `att:` 文本路径。

## Impact

- **Server**（`packages/server/src/Mohist.Server/`）：`AgentSessionInputRecord` / `AgentTurnRecord`（`Sessions/Domain/AgentSession.cs:486,504`）需承载附件记录；`AgentSession.Transitions`（`Sessions/Domain/AgentSession.Transitions.cs` AcceptFollowup）与 launch/follow-up 命令（`Agent/Grains/IAgentSessionGrain.cs` AcceptFollowupCommand、`Agent/Grains/IAgentJobGrain.cs` AgentJobInput）需携带附件并做逐项校验与结果呈现；launch / follow-up 路由（`Api/AgentSessionLaunchRoutes.cs` AllowedTopLevelFields、`Api/AgentSessionFollowupRoutes.cs` GenericFollowupRequest）需接受显式附件并放宽「prompt 必填」为「文本或至少一个可用附件」。附件服务（`Issue/Services/Attachments/AttachmentService.cs` BindIssueAsync / OpenIssueContentAsync / CleanupExpiredPendingAsync、`Api/AttachmentRoutes.cs`）需新增 Agent 输入所有权绑定、Agent 侧内容读取路由与按所有者的保留 / 清理；dispatch 契约（`AgentLauncher.cs`、`Sessions/Services/FollowupDelivery.cs` FollowupDeliveryRequest、`Runner/Services/SignalR/RunnerFollowupDeliveryDispatcher.cs`）需把附件内容 / 引用传给 Runner。
- **Runner**（`packages/runner/src/`）：`runtime/agent-job-executor.ts`（readPrompt、execution-envelope）与 `server/followup-handler.ts` 需接受附件、在所属 Turn 内解析为 Runtime 可读内容而非透传 URL；`session-target.ts` ReceiveFollowupPayload 与 launch payload 需携带附件。不得依赖调用方临时地址或凭据。
- **Web**（`packages/web/src/`）：`pages/agent-session-composer/ui/AgentSessionComposerPage.tsx`（handleLaunch）改为显式发送附件归属而非仅内联 `att:` 文本；`entities/agent/api/agent-sessions.ts`（launchAgentSession、postGenericFollowup）携带附件；`widgets/coder-session/ui/SessionFollowupComposer.tsx` 接入附件选择、预览与逐项结果；复用 `shared/ui/attachment-composer/`。
- **CLI**（`packages/cli/Mohist.Cli/`）：`MohistCliCommands.Agent.cs`（BuildLaunch）与 `MohistCliCommands.Session.cs`（BuildFollowup）新增附件参数并在提交前展示受理结果。
- **Testing**：覆盖纯附件输入、逐项受理结果与部分失败呈现、跨 Session / 用户 / Connection 不可复用、保留 / 清理、Runner 内容解析与不泄露临时地址 / 凭据；使用 fake 附件存储、可注入时间与 Runtime，不触碰真实外部依赖。
