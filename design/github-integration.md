---
status: wip
---

# GitHub 集成

GitHub 集成让 GitHub 承担需求入口、进度公告板与审批来源三个角色。本文定义组件边界：
入站接收与验签、事件归一化、供料 / 关闭 / 审批三个翻译器、回写器与凭据边界。
产品行为见 [`docs/github.md`](../docs/github.md)；PR 交付动作族见
[`workflow/actions.md`](workflow/actions.md) 与 [`docs/actions/github-pr.md`](../docs/actions/github-pr.md)，本文不重复。

它不是什么：不是双向同步（GitHub 侧编辑不回读），不是 GitHub Projects 集成，
不替代 Runner 上的 `gh` 交付动作族。

## Model

### GitHubConnection

Project-scoped 资源，归属独立的 GitHub integration supporting context（放置规则同
Slack integration，见 [`domain-analysis.md`](domain-analysis.md)）。它声明「哪个 GitHub
仓库以什么策略接到哪个 Project」，不持有执行状态。

| 字段 | 说明 |
|---|---|
| `Id` / `ProjectId` | 身份与所属 Project |
| `Owner` / `Repo` | GitHub 仓库坐标；`(Owner, Repo)` 全 server 唯一——一个 GitHub 仓库只连一个 Project |
| `RepositoryName` | 绑定的 Repository 资源名；connect 时按 git URL 匹配已注册仓库，写入时校验存在 |
| `IntakeLabel` | 供料标签，默认 `mohist`；不得以 `mohist:` 为前缀（回写标签族保留该前缀） |
| `FeedMode` | `start`（默认，供料即启动）/ `backlog`（仅入 backlog） |
| `Approvers` | GitHub login 列表；空列表 = review 审批关闭 |
| `Status` | `Active` / `Disabled` |
| `IdentityKind` | `app`（默认，GitHub App 身份）/ `pat`（降级，fine-grained PAT 仅回写） |
| `InstallationId` | `IdentityKind=app` 时必填；connect 时从 GitHub 安装地址解析 |

凭据不进连接表，经 [`ISecretStore`](../packages/server/src/Mohist.Server/Infrastructure/Security/Secrets/ISecretStore.cs)
加密落库，地址命名空间沿用先例（[`outbound-webhook.md`](outbound-webhook.md)）：

- `SecretStoreAddress(projectId, "<connectionId>:webhook")`：入站验签 secret。
- `SecretStoreAddress(projectId, "<connectionId>:api")`：降级形态的回写 PAT（仅 Issues 读写，无代码权限）。
- `SecretStoreAddress("_server", "github-app:key")`：GitHub App 私钥，复用
  `SecretKind.AppToken`；一套部署一份，为所有 `app` 形态连接共用。

必须一直成立（凭据边界）：

- server 持有的长期 GitHub 秘密只有验签 secret、App 私钥与（降级时）无代码权限的回写 PAT；
- server 不长期持有 GitHub 访问令牌：需要时以私钥签 10 分钟 JWT 换取 1 小时
  installation access token，并按 `repositories` 收窄到目标仓库；内存缓存至临期，不落盘；
- server 直调 GitHub API 仅限 issue 评论、标签、关闭三类回写，不触碰任何 git 内容操作；
- 交付（push / PR）所用的 installation token 由 server 按需签发给 Runner（见「交付令牌
  签发」），Runner 不长期保存它；
  [`RepositoryPolicy`](../packages/server/src/Mohist.Server/Project/Domain/RepositoryPolicy.cs)
  对 gitUrl 的禁凭据约束不受影响。

### GitHubIssueLink

server infrastructure 集成记录（与 Slack conversation mapping 同类，不是聚合事实）：
`(ProjectId, RepositoryName, GithubIssueNumber) → IssueNumber`，另持有回写幂等所需的
已回写状态（当前状态标签、已发节点评论集合）。创建后不可变；它是供料幂等键。

PR 到 issue 的关联不建独立记录：按 workflow branch 命名约定 `mo/issue-N` 从
`pull_request` 事件的分支名解析；解析失败即忽略该事件。

## Semantics

### 入站接收与归一化

`POST /api/github-connections/{connectionId}/ingress`：不依赖 operator token，以
`X-Hub-Signature-256`（HMAC-SHA256，算法同 [`hermes-webhook.md`](hermes-webhook.md)）
对原始请求体验签，密钥取 `:webhook` 条目；验签失败返回 401，不落事件。验签通过即
归一化写入 `IEventStore` 并返回 200，后续处理全异步。

归一化规则：

- `type`：`com.mohist.github.<entity>.<action>`，v1 集合为 `issues.labeled` /
  `issues.closed` / `issues.reopened` / `pull-request.reviewed` /
  `check-suite.completed`，注册进
  [`EventCatalog`](../packages/server/src/Mohist.Server/Infrastructure/Events/EventCatalog.cs)。
- `source`：`/mohist/projects/{projectId}/github-connections/{connectionId}`。
- 谱系：`projectid` 必印；`githubrepo`、`githubissue` 印 GitHub 坐标；GitHubIssueLink
  已存在时另印 `issue`（及该 issue 当时的 `epic`，从 link 记录取出时一并快照）——
  读取的是本 integration context 自己的映射记录，不违反
  [`event-protocol.md`](event-protocol.md)「不为 stamping 跨聚合查询」。
- payload 原样保留 GitHub 事件体；路由不读 `data`（协议规则），payload 仅供消费者取证。

GitHub 投递 at-least-once 且可能乱序：所有消费者必须幂等（见各翻译器）。

### 供料翻译器

durable handler，`[Subscription]` 于 `com.mohist.github.issues.labeled`：

```text
if 事件 label != connection.IntakeLabel: 跳过
if GitHubIssueLink 已存在: 跳过                          # 幂等
issue = 创建 Issue(标题/正文快照,
                 目标仓库 = connection.RepositoryName,
                 优先级 = 事件标签中的 p0–p4,
                 来源 = GitHub 坐标)
写入 GitHubIssueLink
if FeedMode == start:
    启动 issue
    启动被拒（prerequisite 未满足 / 仓库不可用）-> 留在 backlog，回写一条说明评论
```

### 关闭翻译器

`[Subscription]` 于 `com.mohist.github.issues.closed`：link 存在且 issue 非终态 →
cancel 该 issue。

自环天然安全：Mohist 完成时 issue 先入终态，之后回写才关闭 GitHub issue；回环的
closed 事件命中终态检查即 no-op，无需识别关闭者身份。

### 审批翻译器

`[Subscription]` 于 `com.mohist.github.pull-request.reviewed`：

```text
issue 编号 = 从分支名解析（mo/issue-N）；失败 -> 忽略
if reviewer.login 不在 connection.Approvers: 忽略
if issue 未停在 Check 审批点: 忽略
APPROVED          -> approve(decidedBy = "github:" + login)
CHANGES_REQUESTED -> reject(decidedBy = "github:" + login, message = review 正文)
COMMENTED         -> 忽略
```

审批者名单是确定性配置，翻译器直查；不经 Agent、不经 prompt 判断。

### 回写器

`[Subscription]` 于 issue / workflow 域中对应「开始、到达审批点、阻塞、完成、取消」
的事件，过滤出有 GitHubIssueLink 的 issue，以连接的 GitHub 身份（App installation
token，或降级时的 `:api` PAT）直调 GitHub REST API：

- **状态标签互斥维护**：移除其它 `mohist:*` 标签后打上当前态标签；
- **四类节点评论**：供料确认、到达审批点、完成（交付摘要 + PR 链接）、取消（原因）；
  同节点同 issue 不重复发（已发集合记在 link 记录）；
- **收尾**：completed → close as completed；cancelled → close as not planned。

可靠性：best-effort——失败（非 2xx、网络、超时）记日志并落持久失败记录（形态同
outbound-webhook 失败记录），不重试、不阻塞产线、不回滚状态。认证类失败（401/403）
另在连接上标记为需注意，Web / CLI 可见。

### 交付令牌签发

Runner 在交付任务需要 GitHub 身份时请求令牌：

`POST /api/github-connections/{connectionId}/delivery-token`（`runner` scope，认证模型见
[`auth.md`](auth.md)）：

```text
输入: { permissions: ["contents:write", "pull-requests:write"] }
server:
  连接非 Active 或 IdentityKind != app -> 409（降级形态下交付走 Runner 自有登录）
  以 App 私钥签 JWT -> 换 installation token（repositories 收窄到本连接仓库，
                       permissions 按输入收窄）
  记录签发审计（runnerId、connectionId、permissions、时间）
输出: { token, expires_at, bot_login }
```

v1 不校验「该 runner 当前工作是否绑定此仓库」：令牌已按仓库缩权且 1 小时过期，泄露
半径被限制在该仓库。

Runner 将 token 注入执行环境：`GH_TOKEN` env、git credential 配置、git author identity
对齐为 `bot_login`——否则 PR 由 bot 开启而 commit 作者是用户，归因分裂。token 只活在
进程环境，不落 Runner 磁盘。

## Examples

供料到完成（`FeedMode = start`，审批者名单含 `alice`）：

1. 用户给 `owner/repo#7` 打 `mohist` 标签 → 供料翻译器建 issue 42、写 link、启动。
2. 回写器：评论「已接入，Mohist issue #42」，打 `mohist:in-progress`。
3. 到达 Check 审批点 → 标签换 `mohist:awaiting-approval`，评论提醒。
4. `alice` 在 PR 上 Request changes → 审批翻译器 reject，打回 Build。
5. 修复后再次到达 Check 门，`alice` Approve → Integrate 完成，issue 42 done →
   标签换 `mohist:done`，评论交付摘要 + PR 链接，关闭 `owner/repo#7`。
6. GitHub 回环的 closed 事件到达 → issue 42 已终态，no-op。

路由订阅与 mohist 域事件同语义，无特例：

```text
event.type == "com.mohist.github.check-suite.completed" && event.issue == "42"
```

## Status

全部未实装。落地顺序建议（供 `mohist-explore` 切分）：入站接收 + 归一化 →
供料翻译器 + GitHubIssueLink → 回写器 → 审批翻译器。

开放问题：

- 事件乱序边缘：`closed` 先于 `labeled` 到达时 link 不存在，`labeled` 后到会为已
  关闭的 GitHub issue 建单。v1 接受，观察后再决定是否补「供料前确认 GitHub issue
  仍 open」一步。
- `check-suite.completed` 入事件集合供路由消费；workflow 的 PR checks 等待仍以
  轮询实装，事件化替代列入后续。
- GitHub 评论 @ 触发 Agent 复用 mention 通道（[`agent-mentions.md`](agent-mentions.md)）
  的可行性，随真实需求再评。
- 个人账号仓库上 App 安装令牌创建 PR 的兼容性：有实践报告称受限（PR 创建要求协作者
  角色，而 GitHub App 不能被加为仓库协作者），与 dependabot 等长期实践相悖。实装时先
  用测试 App 验证；若属实，个人仓库补「machine user + fine-grained PAT」身份形态
  （另立设计）。
