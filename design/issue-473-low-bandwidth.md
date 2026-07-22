# Issue 列表低带宽读取与请求隔离

Issue 列表是 project-scoped 的摘要读取模型。它服务于看板、归档列表、CLI
列表和少量汇总读取；Issue 详情仍由 `GET /issues/{number}` 的
`IssueReadModel` 负责。

## Model

### IssueListItem

`IssueListItem` 是 Server、Web 和 CLI 共同遵守的列表契约。它只包含列表和
看板已使用的当前状态、可执行性和紧凑关系摘要：

- `number`、`title`、`status`、`health`、`projectId`、`projectName`；
- `labels`、`priority`、`risk`、`createdAt`、`updatedAt`、`archivedAt`、`completedAt`；
- `approvalState`、`blockedReason`、`workflowRunId`、`workflowStage`、
  `workflowStatus`、`workflowStageProgress`、`workflowProfileId`；
- `prerequisiteNumbers`、`prerequisites`、`isDraft`、`canStart`、`canBeParent`、
  `blocker`；
- `repositoryName`、`repository`、`repositoryProblem`、`primaryEpic`、
  `parentIssueRef`、`childIssuesSummary` 和现有的紧凑 `children` 引用。

列表契约不包含 `body`、`comments`、`attachments`、`feedback`、`agentConfig`、
`model`、`modelVariant`、`stageModels`、`stageModelVariants` 或从每条 Issue 的
变量层合并出来的配置。`workflowProfileId` 是当前有效 profile 的身份，不是
变量展开结果。

`IssueListItem` 的身份是 `(projectId, number)`。`GET /issues` 的 project、状态、
label、priority、repository、parent 和 archived/all 过滤语义保持不变；返回的
顺序仍按 `number` 升序。`GET /issues/{number}` 不改为摘要，也不改变不存在
Issue 时的 404。

### ParentCandidate

`ParentCandidate` 只包含 `number` 和 `title`。它属于一个 project，只返回未归档、
处于 backlog、尚未启动 workflow、且自身没有 parent 的 Issue。Server 按 number
升序返回，Web 不再从完整 Issue 列表推导候选。

### InboxUnreadCount

`InboxUnreadCount` 只包含 `unreadCount`。计数的范围是当前 project 中未归档且
`ReadAt` 为空的 Inbox 行。Inbox 完整读取模型只由 Inbox 页面使用。

## Semantics

### Collection assembly

`GET /issues` 使用 project-scoped `IssueRow` 当前状态和批量关系读取构造
`IssueListItem`：

1. 读取当前 project 的 Issue 状态列和必要的当前状态字段；
2. 批量读取对应 workflow 的当前状态，得到列表需要的 stage、health、approval
   和 progress；
3. 批量读取 parent、child、prerequisite 和 epic 关系，得到紧凑关系字段；
4. 应用请求过滤、排序并序列化 `IssueListItem`。

列表路径不可调用 `EnrichAsync`，也不可读取 Issue comments、issue/comment
attachments、issue workflow variables、全局/project variables 或 workflow
feedback/history。Comments、attachments、feedback 和变量只在详情或对应的
独立 API 中读取。列表成本因此不会随无关 comments、attachments 或历史数量
增加；关系读取必须是批量的，不能产生按 Issue 的 `AnyAsync`/HTTP 查询。

### HTTP endpoints

在 `/api/projects/{projectRef}/issues` 下增加：

```text
GET /parent-candidates
  data: [{ number, title }]
```

在 `/api/projects/{projectRef}/inbox` 下增加：

```text
GET /unread-count
  data: { unreadCount }
```

两个 endpoint 都经过现有 project resolution filter。未知 project 仍由 filter
返回 404；不能由 SPA fallback 生成成功响应。

### Web query namespaces

Web 使用独立的 key factory，不能用一个 `['issues']` 前缀代表所有资源：

```text
issue-list       project + list filters
issue-detail     project + issue number
issue-workflow   project + issue number + workflow subresource
issue-artifacts  project + issue number + artifact subresource
issue-candidates project
inbox-list       project
inbox-count      project
```

详情、workflow、artifact、candidate 和 inbox list/count 的失效必须只命中各自
namespace。所有 TanStack Query `queryFn` 将 `context.signal` 传入 API client，
API client 再通过公共 `request` 的 `RequestInit.signal` 传给 `fetch`。

Create Issue dialog 由 `open` 条件控制挂载。关闭时没有 dialog component、候选
query 或完整 issue-list query；打开后只请求一次 project-scoped
`parent-candidates`（遵守正常 Query cache 语义），并保留候选的现有选择和
失效清理行为。Prerequisite picker 在用户展开前不读取 issue list；展开后才按需
读取可搜索的压缩摘要。创建成功只失效受影响 project 的活动列表、候选列表和
受影响 parent 的详情/关系资源。

Inbox shell badge 使用 `unread-count`；Inbox 页面继续使用完整 `/inbox`。读、读
全部和归档操作同时失效 `inbox-list` 与 `inbox-count`，所以 badge 和页面都由
HTTP 真值重新协调，实时 hint 不合成 Inbox item。

### Event-to-resource invalidation

事件 envelope 中的 `projectId` 和 `issueNumber` 是失效路由的输入。事件没有
当前 project 的 projectId 时不触碰该 project 的 cache；有不同 projectId 时
直接忽略。具有 issue number 的事件只失效该 `(projectId, issueNumber)` 的
detail/workflow/artifact key，不使用 issue number 无关的 broad key。

- Issue 创建、归档、取消、重开、开始、完成、draft、label、priority、
  prerequisite、workflow profile、repository、epic、parent 和 composite 状态变化：
  失效该 project 的活动 list；创建、parent 和 candidate eligibility 变化还失效
  `issue-candidates`。parent 变化同时精确失效 previous/current parent 的 detail；
  其他事件只失效事件 Issue 的 detail 及受影响的关系资源。
- Workflow run、stage、approval 和 task 事件：失效事件 Issue 的 detail、
  workflow 和该 project 的活动 list。Artifact 事件只失效对应 artifact/workflow
  资源。
- Agent session 事件只失效对应 session/workflow/detail 资源以及已有的 agent
  activity/status 资源。
- Inbox hint 只失效当前 project 的 `inbox-list` 和 `inbox-count`。

列表 project key 的失效采用 TanStack Query 默认的 active refetch 语义；不活动
列表只标记 stale，不发起网络请求。详情 key 使用 issue number 精确匹配。
因此 Issue #474 的事件不会重新请求当前查看的 Issue #473 详情；不涉及列表
结构的无关事件 burst 不会重新请求当前 detail 或 collection。所有重新读取都
以 HTTP endpoint 为真源，不从事件 payload 合成列表或详情状态。

### Cold transfer and static serving

Vite production build 开启 minification 并关闭 source map。`App` 保留 shell、
providers 和 route table 的静态入口，但每个 page route 使用
`React.lazy`，由一个 `Suspense` boundary 承载；Create Issue dialog 也使用
lazy import。页面模块不得因 App 的静态 import 被 cold entry 下载。

Server 注册 Brotli 和 gzip response compression，使静态 JS/CSS 与 JSON 在客户
端支持时压缩。静态文件规则为：

- `/assets/*`：`Cache-Control: public,max-age=31536000,immutable`；
- `index.html` 和 SPA fallback：`Cache-Control: no-cache`；
- `/api/*` 和 `/otel/v1/*` 不进入 SPA fallback，未知路径返回 404。

生产 build assertion 检查：所有 HTML 引用的 assets 是带 fingerprint 的静态文件、
没有 source map 产物、存在独立 route chunks，并打印/验证 initial compressed
budget 与 route chunk compressed budget。Server static-serving spec 检查
assets、HTML fallback 和 API 404 的 headers/status。

## Examples

### List response boundary

给定 Issue 有 20 条 comments、12 个 attachments、多个 feedback/history 和一组
workflow variables，`GET /issues` 仍只返回 `IssueListItem`；这些数量不会改变
列表关系 assembly 的工作项。`GET /issues/{number}` 仍返回 detail 所需的
body/comments/attachments/feedback 等字段。

### Event isolation

当前 project 正在查看 `473`，cache 中有 detail key `('project', 473)`。收到带
`projectId = project`、`issueNumber = 474` 的 workflow event 时，只能标记
`474` 的 detail/workflow key（以及该事件明确需要的 active list）；不得命中
`473` 的 detail key。收到另一个 project 的同号 event 时，不触碰当前 project
的任何 issue key。

### Closed dialog

首次渲染 shell 且 `createIssueOpen = false` 时，Network 中没有
`GET /issues` 或 `GET /parent-candidates`。打开 dialog 后发出一个
`GET /parent-candidates`，响应中的每个对象只有 `number`、`title`。

## Status

本 spec 是 issue-473 low-bandwidth 优化的实现目标，已由本工作区的代码与测试
落地。后续如果列表字段或事件契约扩展，必须同步更新本 spec、对应的 Server/Web
类型和行为 spec。
