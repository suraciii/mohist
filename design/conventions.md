# Conventions

## 身份

领域身份是能够永久、无歧义地指向一个实体的最小键。它不要求是单个随机 id；当实体
天然属于父级作用域时，父级身份与作用域内编号共同组成身份。

| 概念 | 领域身份 | 示例 |
|---|---|---|
| Project | `ProjectId` | `proj_123` |
| Issue | (`ProjectId`, `IssueNumber`) | (`proj_123`, `42`) |
| Epic | (`ProjectId`, `EpicNumber`) | (`proj_123`, `7`) |
| WorkflowRun | `WorkflowRunId` | `wr_123` |
| Runner | `RunnerId` | `runner_123` |
| AgentSession | `SessionId` | `session_123` |
| SessionOperation | `operationId` | `op_123` |
| Event | `EventId` | `evt_123` |
| Principal | `PrincipalId` | `prin_123` |
| Credential | `CredentialId` | `cred_123` |

- Issue 与 Epic 的 number 是 Project 内永久身份的一部分，不是展示别名；不再为它们
  维护第二个随机 id。
- GrainKey 必须从领域身份无损、统一地编码，并能还原为同一个强类型身份。作用域身份
  使用公共 codec，不在调用点手拼 `projectId:issueNumber` 一类字符串。
- ResourceKey 用于 HTTP 资源路径，也可以作为 CloudEvents `source`；它不作为另一套实体
  身份写入扩展属性、锁或审计字段。
- 外部名称可以解析到身份，但解析结果不得产生另一套实体身份。

## Role suffixes

| Suffix | Scope | Example |
|---|---|---|
| Querier | single-domain read projection | IssueQuerier |
| Assembler | cross-domain read assembly (AgentOps) | AgentActivityFeedAssembler |
| Reporter | cross-domain metrics (AgentOps) | AgentUsageReporter |
| Resolver | external name → canonical resource | ProjectResolver |
| Manager | config or lifecycle policy | WorkflowProfileManager |
| Store | persistence boundary for one shape | WorkflowRunStore |

- No new `*QueryService` names.
- Assembler/Reporter belong to AgentOps. Never in leaf domains like Session.

## ResourceKey

```
/projects/{projectId}
/projects/{projectId}/issues/{issueNumber}
/projects/{projectId}/epics/{epicNumber}
/workflow-runs/{workflowRunId}
```

Leading slash. Plural nouns. URL path segments. No trailing slash.

## Entity map

| Concept | Domain identity | GrainKey source | ResourceKey |
|---|---|---|---|
| Project | projectId | projectId | /projects/{projectId} |
| Issue | projectId + issueNumber | projectId + issueNumber | /projects/{projectId}/issues/{issueNumber} |
| Epic | projectId + epicNumber | projectId + epicNumber | /projects/{projectId}/epics/{epicNumber} |
| WorkflowRun | workflowRunId | workflowRunId | /workflow-runs/{workflowRunId} |
| Runner | runnerId | runnerId | /projects/{projectId}/runners/{runnerId} |
| WorkflowBacklog | — | projectId | /projects/{projectId}/workflow-backlog |
| StageLock | — | internal id | /projects/{projectId}/workflow-stage-locks/{resource} |
| AgentSession | sessionId | sessionId | /projects/{projectId}/agent-sessions/{sessionId} |
| Event | eventId | — | /events/{eventId} |

## AgentSession runtime identity

`sessionId` is Mohist's stable logical AgentSession identity. A runtime-owned physical
Session is identified separately:

Concept ownership and origin rules are defined in
[`agent-execution.md`](agent-execution.md).

```json
{
  "runtime": "opencode",
  "runtimeSessionId": "ses_..."
}
```

- Use `runtimeSessionId` for the external physical identity. Never use `acpSessionId` or
  `coderSessionId` as aliases.
- `workflowRunId + sessionName` and `agentId` are origin/lookup references, not AgentSession
  identity. Workflow- and Agent-scoped routes resolve to the canonical `sessionId` resource.
- `runtime` names the execution backend. Do not add a second `kind` field.
- Current runtime binding retains `runnerId`; immutable `workDir` belongs to AgentSession. Together
  they let Session commands survive Runner process restart. A Workflow adapter rejects a request
  whose authoritative workspace differs from the AgentSession workDir; it never silently reuses
  another directory.
- A complete current binding is `(runnerId, runtime, runtimeSessionId, bindingEpoch)`. Every binding
  replacement compares that complete expected binding and the AgentSession workDir. `bindingEpoch`
  is monotonic and changes whenever current binding is replaced; it is part of every command/event
  fence, not a display-only revision.
- A binding operation also carries the complete operation fence
  `(operationId, ownerId, ownerFence, claimGeneration, expectedBinding, candidateBinding, phase,
  leaseUntil, deadlineAt)`. `ownerFence` and `claimGeneration` are independent monotonic values;
  an unqualified `generation` in an implementation means `claimGeneration`, never
  `ContextGeneration`. Every phase write, candidate create/get/discard/cleanup, binding CAS and
  completion must compare the full fence and expected binding. Before any external side effect,
  Server atomically rechecks that fence, the current owner lease, and the current binding, then
  passes the same token to Runtime/provider. A stale owner fails closed before create, submit,
  discard, or complete.
- Confirmed-missing recovery stays on the bound Runner and only replaces `runtimeSessionId` while
  incrementing `ContextGeneration` for the new logical context. `rebind` cannot change `runnerId`;
  Runner handoff is an explicit `handoff` operation and is not missing recovery. An adopted candidate
  is current binding and cannot be removed by an old cleanup.
- AgentSession stores only the current binding. It does not expose or persist a physical Session
  history model.
- Compact does not change `runtimeSessionId` or `ContextGeneration`; it persists a ContextBoundary
  and operation result. Reset, runtime change, confirmed missing recovery, or force-reset replaces
  the current binding while preserving `sessionId` and starts a new `ContextGeneration`. A work
  directory change requires a new logical Session identity.

### Session operation contract

`Compact`, `Reset`, confirmed-missing recovery, `force-reset`, `handoff` and `rebind` are durable
Session operations. Their caller supplies a reusable `operationId`; Server never creates an
unqueryable operation key for a command response. The canonical operation read contains:

```text
operationId
kind = compact | reset | recovery | force-reset | handoff | rebind
phase
outcome = pending | succeeded | rejected | failed | unknown
ownerId
ownerFence
claimGeneration
revision
deadlineAt
nextAction
```

`launchRequestId` is a different, client-supplied launch identity. Server creates
`launchOperationId` exactly once when first preparing that request and durably maps
`launchRequestId -> launchOperationId`; it is never the client key and is never required to
discover a launch after response loss.

`ContextBoundary.Kind` is `compact | reset | runtime-change | missing-recovery | force-reset |
handoff | rebind`. Compact keeps `ContextGeneration`; reset, runtime change, missing recovery,
force-reset, handoff and rebind increment it after their binding/context commit. A RunnerId change
requires a durable `handoff` operation; same-Runner replacement requires `rebind` or confirmed
missing `recovery`. Reconnect, timeout, or a stale event cannot infer either operation.

## WorkflowRun metadata

```
WorkflowRun.Metadata
  ProjectId
  IssueNumber
  EpicNumber?
```

这三个值是 WorkflowRun 在本地保存的 Issue 上下文，不是 Issue 或 Epic 的第二份权威
状态。Issue 启动 WorkflowRun 时提供当前上下文；归属后来变化时，持久事件触发幂等命令
刷新 `EpicNumber`。刷新前已经产生的事件保留生产者当时持有的上下文。

不增加 lineage revision、binding 状态或通用 owner/controller 引用。跨聚合重投递时，
handler 重新读取 Issue 当前状态，再把完整上下文交给 WorkflowRun；旧事件因此不会把旧
归属重新写回。

## Dispatch namespaces

Runtime context、Workflow Variables、Project Prompts 和 Project Repository resources 具有
不同所有者和生命周期，不合并成一个 config 或 Variables document。各命名空间的解析时机
以 [`workflow/task-dispatch.md`](workflow/task-dispatch.md) 为准。
