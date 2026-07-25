---
status: wip
---

# 评论提及（Comment Mention）

在 issue comment 里 `@<agent 名>` 直接启动一个 Mohist Agent：提及是 Agent 的
第三条触发路径（手动 launch、路由规则之外的第三种），零配置——正文里的点名
就是路由决策，不需要路由表。

事件协议与 stamping 见 [`event-protocol.md`](event-protocol.md)；启动管线、
AgentJob 与 AgentSession 见 [`agent-execution.md`](agent-execution.md) 与
[`event-routing.md`](event-routing.md)。

## Model

提及不引入新的领域资源。涉及的概念都是已有的：

- **评论**：Issue 的普通 comment，新增一个事件族 `com.mohist.issue.comment-added`，
  按 `issue.*` 族 stamping（`projectid`、`issue`、`epic` 如有）。payload 携带
  `commentId`、`author`、`body`。
- **提及 token**：评论正文中 `@` 后跟 Agent 名，按空白与标点取词，大小写不敏感。
  只按名字解析，不解析 id。
- **触发**：一次提及 = 一次普通 Agent 启动（AgentJob + Agent launch 来源的
  AgentSession），prompt 为评论正文全文（`@` token 原样保留，不作剔除）。

只有 issue comment 触发提及。Issue 正文里的 `@` 是引用不是点名，不触发。

## Semantics

### 检测与启动

```text
AddCommentAsync 持久化评论 -> 发射 issue.comment-added
MentionDispatchHandler（系统 handler，订阅该事件）:
  if comment.author 与项目内某 active Agent 同名: 结束        # 见「防循环」
  names = 解析 body 中的 @token，去重
  for each name:
    agent = 按名字解析项目内 active Agent
    if 解析失败: 记结构化日志，继续下一个                      # 见「解析失败」
    launch(agent, prompt = body, context = issue, key = hash(projectId, commentId, agentId))
```

- 启动走共享 launcher 的 manual（workspace-optional）路径，不复用路由启动
  管线：issue 上下文作为 session metadata 记录，但不做 workspace 解析、不做
  preflight。理由是提及的典型用例是在 backlog issue 上 `@supervisor` 推进它，
  backlog issue 没有 workflow run 也没有 workspace —— 路由路径会 preflight
  失败并把这条最该触发的提及变成失败 AgentJob。触发标签记 `comment-id` 与
  `comment-added` 事件 id，从 comment 与 AgentJob 双向可查，且可区分于路由 /
  watch 启动。
- 幂等键含 commentId：同一评论的重复分发不会重复启动；一条评论 @ 同一个 Agent
  多次只启动一次；@ 多个不同 Agent 各启动各的。
- 提及启动是一次性 AgentJob。owner 要求的是持续关注时（例如「监督并推进这个
  issue」），由 Agent 用自己的命令面兑现——`mo issue watch add` 把该 issue
  加入关注（见 [`issue-watch.md`](issue-watch.md)）——系统不把提及展开成
  持久订阅。

### 防循环

约定：Agent 写评论时 `--author` 声明自己的名字（预设文本已写入该约定）。
凡 author 与项目内 active Agent 同名的评论，不做提及检测。由此 Agent 的评论
既不会触发别人，也不会触发自己，提及链只能在人的评论处开始。

author 是声明而非认证：人故意用 Agent 的名字署名，其评论同样不触发。本地单
用户场景下这是可接受的约定成本。

### 解析失败

`@` 了不存在的名字 = 普通评论，不启动任何东西，记结构化日志。人可以从
`mo agent job list <agent>` 确认 Agent 是否被启动；名字拼错时唯一信号是
「没有动静」。显式的 typo 反馈（系统回复评论或 inbox 条目）留作开放问题，
见 Status。

## Examples

```text
# owner 在 issue #42 的评论区写道（Web 或 mo issue comment add 均可）：
@supervisor 监督并推进这个issue

# 系统：supervisor 启动一次 AgentJob，prompt 为该评论全文，上下文为 issue #42。
# Agent 读到「监督并推进」后，典型动作：
#   mo issue start 42
#   mo issue watch add 42 --agent supervisor
# 并在 comment 里以 [supervisor] 记录自己的安排。
```

## Status

issue-490 已落地本文描述的全部行为：

- `AddCommentAsync` 持久化评论后，在同一事务里发出 lineage-stamped
  `com.mohist.issue.comment-added` CloudEvent（payload：`commentId` /
  `author` / `body`；lineage：`projectid` + `issue`，`epic` 如有）。
- `MentionDispatchHandler` 订阅该事件，按本文规则做防循环、token 解析、
  名字解析（大小写不敏感，含 `AgentQuerier.GetByNameAsync` 已改为大小写
  不敏感）、解析失败 no-op、comment-anchored 幂等启动。
- `IAgentLauncher.LaunchMentionAsync` 是 manual 路径的 mention 入口，session
  id / AgentJob key 由 `AgentSessionResolver.CommentSessionId` /
  `CommentJobKey` 按 `hash(projectId, commentId, agentId)` 派生；trigger 标签
  记 `mohist.io/trigger/event-id` + `mohist.io/trigger/comment-id`。
- `muted` watch 不抑制提及启动（见上文「检测与启动」与 issue-490 design
  Decision 7）：handler 不读 `WatchEntryStore`。

### 开放问题

未被解析的提及是否需要显式反馈（系统评论 / inbox 条目），先以结构化日志与
「没有动静」为现状，待真实使用数据决定。

### 已实装、本文依赖的底座

`IAgentLauncher` 双路径启动与幂等键、`AgentSessionResolver` 的 comment-anchored
stable key、评论的 `author` 字段（`AddCommentAsync(author, body)`）、
`design/event-response.md` 的 comment author 归属约定。
