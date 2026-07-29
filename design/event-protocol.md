---
status: converged
---

# 事件协议（Event Protocol）

本文定义 Mohist 事件信封的统一协议：任何实体的关键事件都可以被同一个路由器、
同一种表达式订阅到。事件的持久化与分发机制见 [`eventbus.md`](eventbus.md)；
Agent 侧的消费面（路由表）见 [`event-routing.md`](event-routing.md)。

## 三个正交轴

每个事件信封回答三个问题，各归一个属性，不混用：

| 轴 | 信封属性 | 回答什么 |
|---|---|---|
| What | `type` | 发生了什么 |
| Who | `source` | 哪个实体发生的 |
| Where | context 扩展属性 | 发生在哪条业务链上 |

`type` 与 `source` 已有稳定约定；本协议的核心增量是第三轴——**业务谱系（context）
的强制 stamping**，它让「订阅 issue #42 名下的一切」成为一条表达式。

## type：事件分类法

`com.mohist.<域>.<事件>`，注册于 `EventCatalog`。Catalog 只回答有哪些稳定事件 type；
业务谱系要求由生产者所属的事件族和事件结构决定，不在每个 type 上重复登记一份属性表。

## source：发射实体

source 使用发射实体的领域身份：`/mohist/workflow-runs/{workflowRunId}`、
`/mohist/projects/{projectId}/issues/{issueNumber}`、
`/mohist/projects/{projectId}/epics/{epicNumber}`。Issue 与 Epic 的 Project 作用域是其
身份的一部分；可变的 Epic 归属、Workflow 来源等业务谱系不编码进 source。

## context 属性：业务谱系 stamping

### 规则

1. **生产时印全**：每个事件在产生的那一刻，由 store 层把生产聚合当时持有的业务
   谱系印成扁平扩展属性。Issue 从自己的 `EpicNumber?` 取值，WorkflowRun 从自己的
   Issue 上下文取值；**不允许为 stamping 发起跨聚合查询**。
2. **路由 envelope-only**：matcher 和分发器只读信封，永不反查业务域。领域 reaction
   handler 可以在执行幂等命令前读取当前聚合状态，但不得改变路由是否命中的结果。
3. **快照真相**：属性记录的是生产时刻的归属。issue 后来挪了 epic，历史事件不改写。
4. **准入标准**：凡是值得作为路由维度的业务身份，就提升为信封属性；payload
   （`data`）永不参与路由。

### 命名

CloudEvents 扩展属性名限小写字母数字。业务实体使用其唯一身份对应的最短准确名称：

- `projectid`：Project 的全局身份；
- `issue`、`epic`：Project 内的 Issue / Epic 编号，也是它们的领域身份组成部分；
- `workflowrunid`、`agentid`、`sessionid`、`runnerid`：各自的全局身份。

不同时携带 `issue` + `issueid` 或 `epic` + `epicid`。Issue 与 Epic 没有第二套内部 id，
因此也没有 `issueno` / `epicno` 别名。

### Stamping 矩阵

| 事件族 | projectid | epic | issue | workflowrunid | agentid | sessionid | runnerid |
|---|---|---|---|---|---|---|---|
| `workflow.*` | ✅ | 如有 | 如有 | ✅ | – | – | – |
| `issue.*` | ✅ | 如有 | ✅ | – | – | – | – |
| `epic.*` | ✅ | ✅ | – | – | – | – | – |
| `agent-session.*` | ✅ | 如 Workflow 来源且有 | 如 Workflow 来源 | 如 Workflow 来源 | 如 Agent 来源 | ✅ | – |
| `runner.*` | 如有 | – | – | – | – | – | ✅ |
| `inbox.item-persisted` | ✅ | 原事件如有 | ✅ | 原事件如有 | – | – | – |

「如有」= 生产时该归属存在则必印，不存在则省略（不印空值）。

任何结构化携带 Stage 的 Workflow 事件都另印 `stage`（包括 `workflow.stage.*`、
`workflow.task.*`、`workflow.check.*` 与 `workflow.feedback.requested`）——渲染占位符
`{{event.stage}}` 依赖它，不再从 `data` 解析。

`subject` 保留 CloudEvents 原义，不作为路由依据。

## 匹配表达式（CEL 子集）

订阅/路由用一条布尔表达式匹配信封，语法采用 CEL 的一个子集——与
[CEL](https://cel.dev/) 语法兼容，未来需求超出子集时可替换为全量实现，
已存表达式不变。

### 语法

```
expr       := or
or         := and ( "||" and )*
and        := unary ( "&&" unary )*
unary      := "!" unary | primary
primary    := "(" expr ")" | comparison | call | presence
comparison := operand ( "==" | "!=" ) operand
            | attr "in" "[" string ( "," string )* "]"
call       := attr "." func "(" string ")"      func ∈ { startsWith, endsWith, contains, matches }
presence   := "has" "(" attr ")"
operand    := attr | string
attr       := "event" "." ident
string     := 双引号字符串字面量
```

示例：

```
event.type.startsWith("com.mohist.workflow.") && event.issue == "42"
event.type == "com.mohist.workflow.run.failed" && event.stage != "plan"
event.issue in ["42", "43"]
event.type == "com.mohist.issue.completed" && has(event.epic)
```

### 语义

- 所有值都是字符串；`event.<attr>` 解析信封属性（`type`、`source`、`subject`
  与全部 context 扩展属性同权）。
- **缺失属性求值为空串 `""`**；需要区分「缺失」与「空」时用 `has()`。
- `matches` 为正则匹配，求值必须带超时保护。
- 无循环、无函数定义、保证终止；求值确定性（同一事件同一表达式永远同一结果）。
- **写入时编译**：创建/更新时 parse 失败即拒绝。**运行期求值异常按不命中处理**，
  记入结构化日志与计数器。
- 不提供 `event.data.*`：payload 结构属于各域私有，路由层不得耦合。需要按某个
  业务维度路由时，按准入标准把它提升为 context 属性。

### 求值器

自实现（预估 300–400 行 + conformance 测试集），零外部依赖。不引入
`Cel` / `Cel.NET` 等库：求值目标只是扁平 string→string 字典，用不到 CEL 的
类型系统与 protobuf 集成，且这两个库都非社区主流。

## 与分发器、消费面的关系

一个路由器（`eventbus.md` 的单分发器），两类消费者，同一协议：

- **系统消费者**：编译期注册的 `[Subscription]` handler；
- **用户消费者**：Agent 路由表（见 `event-routing.md`）。

两类消费面的匹配机制分工见 `eventbus.md`。**对称性即验收标准**：若某事件
系统 handler 能路由到而用户表达式订不到，即协议破损。

## Conformance

- `EventCatalog` 只维护事件 type，不承担第二份谱系矩阵；
- 生产规则按聚合事件族定义：WorkflowRun、Issue、Epic、AgentSession、Runner 各有一组
  基础必填上下文；Inbox 派生事件继承原事件上下文；`stage` 由事件是否结构化携带 Stage
  决定，而不是手列 type 名称；
- 一组 spec 测试遍历每个实际事件生产路径，按生产者事件族和事件结构断言信封。新增
  producer 或新增可发射事件忘印谱系时测试即红，不需要 `CatalogOnlyTypes` 例外名单；
- 表达式求值器有独立 conformance 测试集（语法、缺失属性、正则超时、确定性）。

## Status

已实装：三轴信封与事件 catalog、业务谱系 stamping（各生产者 Lineage +
ProducerConformance 覆盖事件生产路径）、CEL 子集求值器与用户侧路由求值、
`stage` 属性提升。Issue / Epic 双身份与 `issueid` / `epicid` / `issueno` /
`epicno` 旧属性已随 issue #412 移除。
