# Conventions

本文记录跨模块约定。新的设计文档和代码命名优先遵守这里。

## Identity Terms

| Term | Meaning | Example |
|------|---------|---------|
| EntityId | 领域实体自己的稳定身份 | `issueId`, `workflowRunId` |
| GrainKey | Orleans actor address | `workflowRunId` for `WorkflowGrain` |
| ResourceKey | RESTful resource path | `/projects/{projectId}/issues/{issueId}` |

Rules:

- `EntityId` is stable and singular.
- `GrainKey` follows the entity owned by the grain.
- `ResourceKey` is a URL/resource-path convention only. Do not force it into event rows, locks, or audit data.
- Parent scope belongs in metadata or `ResourceKey`, not in `EntityId`.
- Display numbers and route aliases are lookup keys, not entity identity.
- Avoid ad hoc keys like `projectId:issueNumber` in new designs.

## Role Names

| Suffix | Use For | Example |
|--------|---------|---------|
| `Querier` | 单域只读投影/查询边界 | `IssueQuerier` |
| `Assembler` | 跨域只读报告组装（AgentOps） | `AgentActivityFeedAssembler` |
| `Reporter` | 跨域只读指标计算（AgentOps） | `AgentUsageReporter` |
| `Resolver` | 别名/外部 key 换算规范身份 | `IssueIdentityResolver` |
| `Manager` | 拥有配置或生命周期策略 | `WorkflowProfileManager` |
| `Store` | 单一状态形态的持久化边界 | `WorkflowRunStore` |

Rules:

- Do not introduce new `*QueryService` names.
- `Assembler` / `Reporter` 属 AgentOps，允许依赖全部业务域；不要把它们放进单域（尤其 Session 叶子域），依赖不变量见 [`domain-analysis.md`](domain-analysis.md)。
- Keep resolvers narrow: they do not enrich DTOs or compute workflow state.

## ResourceKey Format

```text
/projects/{projectId}
/projects/{projectId}/issues/{issueId}
/workflow-runs/{workflowRunId}
/projects/{projectId}/workflow-backlog
```

Rules: leading slash; plural resource names; URL path segments（不用冒号分隔串）; no trailing slash; encode unsafe segments.

## Entity Map

| Concept | EntityId | GrainKey | ResourceKey |
|---------|----------|----------|-------------|
| Project | `projectId` | `projectId` | `/projects/{projectId}` |
| Issue | `issueId` | `issueId` | `/projects/{projectId}/issues/{issueId}` |
| WorkflowRun | `workflowRunId` | `workflowRunId` | `/workflow-runs/{workflowRunId}` |
| Runner | `runnerId` | `runnerId` | `/projects/{projectId}/runners/{runnerId}` |
| WorkflowBacklog | none | `projectId` | `/projects/{projectId}/workflow-backlog` |
| StageLock | none | internal lock id | `/projects/{projectId}/workflow-stage-locks/{resource}` |
| AgentSession | `sessionId` | `sessionId` | `/projects/{projectId}/workflow-runs/{workflowRunId}/sessions/{sessionName}` |
| Event | `eventId` | none | `/events/{eventId}` |

## WorkflowRun Metadata

`WorkflowGrain` is keyed by `workflowRunId`. `WorkflowRun` owns a run instance, not an issue slot——owner identity 放 metadata：

```text
WorkflowRun.Metadata
  ProjectId
  IssueId
```

- `IssueNumber` 是展示/路由 lookup 值，不进 workflow metadata。
- Event append、event query、locks、scheduling 一律用 `workflowRunId` / `issueId`；API 路由若收到 issue number，在边界处解析为 `issueId`：

```text
/projects/{projectId}/issues/{number}
  -> IssueIdentityResolver.GetIdAsync(projectId, number)
  -> issueId
  -> GrainKey.Issue(issueId)
```

## Runtime Context vs Profile Variables

Use `WorkflowRuntimeContext`, not `WorkflowRuntimeVariables`.

| | Runtime context | Profile variables |
|---|---|---|
| 回答 | "这次 dispatch 需要哪些具体 run 事实？" | "这个 workflow run 该如何被参数化？" |
| 内容 | issue title/body、repository/workspace、prompt/template 输入、run facts 快照 | template 内嵌 < project profile < issue profile < dispatch 注入 |
| 归属 | run-start 快照，用于渲染 dispatch payload | `WorkflowProfileManager` 管理的可配置输入 |

Runtime context is not identity（identity 在 `WorkflowRun.Metadata`）, and not profile configuration. Keep the lifecycles separate.

## 差距脚注

- 存量 run 可能仍带 legacy `issueKey = projectId:issueNumber`；读路径只允许把它当 fallback，新 run 一律写 `issueId`。
