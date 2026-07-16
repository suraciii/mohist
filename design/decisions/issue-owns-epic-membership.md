# Issue 持有 Epic 归属（issue-412）

## 背景

旧模型同时保留 Issue/Epic 的随机 id 与 Project 内 number，并把 Epic membership 放在
Epic 侧关系表。事件路由又需要把 Issue、Epic 谱系复制到 Issue 和 WorkflowRun。结果是
同一个事实有多种身份、多个写入入口，还需要 binding、revision 和补偿协议维持副本。

需要在以下约束下收敛模型：

- 聚合是强一致性和数据库事务边界；
- Issue 与 Epic 位于同一限界上下文，允许相互依赖；
- 一个业务事实只能有一个写入权威；
- 模型只保留实际需要的概念和属性。

## 决策

### 1. number 就是 Issue / Epic 身份

Issue 身份为 (`ProjectId`, `IssueNumber`)，Epic 身份为 (`ProjectId`, `EpicNumber`)。
删除它们的随机 `IssueId` / `EpicId`，不再把 number 当成需要解析到另一身份的别名。
Orleans GrainKey 和资源路径都从该领域身份统一派生。

### 2. Issue 是当前 Epic 归属的唯一写入权威

Issue 直接持有 nullable `EpicNumber`。一个 Issue 同时最多属于一个 Epic，迁移时在
Issue 自己的事务中把旧值替换为新值。Epic 不保存可独立写入的 membership row 或成员
集合；成员列表、进度和推进候选都是对 Issue 当前状态的查询。

关联入口仍可以是 `Epic.LinkIssue`。Epic 先读取 Issue 当前归属；已经属于该 Epic 时按
幂等成功返回，否则校验自己是否接受关联，再同步命令 Issue 执行 `AssignEpic`。真正的
归属只在 Issue 事务中提交。取消关联携带 expected Epic number，避免旧 Epic 的迟到命令
清掉已经迁移的新归属。

### 3. 同一上下文允许相互依赖，但不共享事务

Epic 可以命令 Issue 关联或启动；Issue 的归属、启动、完成事件也可以异步触发 Epic
重算。这个业务闭环不等于同步调用环：一次调用栈只有 Epic → Issue，反向路径必须在
Issue 提交后由 durable event 发起。

任何数据库事务都只能包含一个聚合状态及该聚合自己的领域事件。Epic 状态变化、Issue
归属变化和 WorkflowRun 变化分别提交。跨聚合中途失败不回滚已经提交的聚合，而是依靠
事件重投和幂等命令收敛。

### 4. WorkflowRun 只保存最小 Issue 上下文

WorkflowRun 需要为事件 stamping 保存
`{ ProjectId, IssueNumber, EpicNumber? }`。这是运行所需的本地上下文，不是归属权威。
Issue 启动 run 时一次提供；归属变化后，handler 重新读取 Issue 当前状态并幂等刷新
active WorkflowRun。

不增加 `AwaitingBinding`、`WorkflowBindingPending`、lineage revision 或 binding 协议。
`IssueWorkStarted` 本身是可靠交接：handler 用 Issue 已保存的 `WorkflowRunId` 调用
`WorkflowRun.EnsureStarted`，重复投递由 run identity 幂等处理。

### 5. 不引入通用 owner / controller 模型

Kubernetes 的 `ownerReferences` 与 controller 解决通用资源级联管理；这里的业务关系只有
一个明确含义：Issue 当前属于哪个 Epic。`EpicNumber?` 已经完整表达该事实。泛化为
`OwnerRef { Type, Id }` 会引入未使用的多 owner、级联删除、controller 仲裁和通用协议，
同时隐藏领域语言，因此不采用。

## 失败如何恢复

| 失败位置 | 已提交事实 | 恢复方式 |
|---|---|---|
| Epic 校验后、Issue 提交前 | 无新归属 | 重试 `LinkIssue` |
| Issue 提交后、响应 Epic 前 | Issue 已有新 `EpicNumber` | 重试命中 `AssignEpic` 幂等结果；`IssueEpicChanged` 仍会投递 |
| Epic 重算保存失败 | Issue 归属已成立 | durable handler 重投 `Epic.Recompute` |
| Issue 启动后、WorkflowRun 创建前 | Issue 已保存 `WorkflowRunId` | `IssueWorkStarted` 重投 `EnsureStarted` |
| 旧归属事件迟到 | Issue 可能已有更新归属 | handler 重读 Issue 当前状态，不使用旧 payload 覆盖 |

没有一种恢复需要跨聚合事务、分布式锁或人工修复中间状态。

## 后果

- Issue/Epic 命令、资源路径和事件只使用 Project-scoped number，模型与用户语言一致。
- Epic 查询可能短暂过期；Issue 在命令入口重验不变量，因此过期只导致 no-op、拒绝或
  重试，不会破坏一致性。
- Epic 的进度和自动推进对 Issue 提交是最终一致的，而每个 Issue、Epic、WorkflowRun
  自身状态仍保持强一致。
- 旧 `EpicIssueRow`、`IssueId` / `EpicId` 以及为其副本同步引入的 binding/revision 状态
  都应在迁移完成后删除，不保留双写兼容层。

本决策保留 [`epic-status-revival.md`](epic-status-revival.md) 的产品语义，但取代其中
“唤醒与 membership row 同事务”的实现方式。
