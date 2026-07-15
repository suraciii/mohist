---
status: wip
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

`com.mohist.<域>.<事件>`，注册于 `EventCatalog`。`EventCatalog` 从常量表升格为
**协议注册表**：每个 type 除名字外，还声明它必须携带的 context 属性
（见下文 conformance）。

## source：发射实体

`/mohist/<entity>/<id>`，如 `/mohist/workflow-runs/{runId}`、`/mohist/issues/{issueId}`。
source 只表达「谁发出」，是实体自我身份；**不承载谱系**。谱系一律走 context 属性，
不编码进 source 路径——路径强制单一层级和固定顺序，而 Mohist 的实体关系不是纯树
（issue 的 epic 可选可变，AgentSession 的来源可能是 Workflow 或 Agent）。

## context 属性：业务谱系 stamping

### 规则

1. **生产时印全**：每个事件在产生的那一刻，由 store 层把当时已知的业务谱系印成
   扁平扩展属性。谱系来自聚合自身状态或已有 annotations（如 run metadata 里的
   issueId），**不允许生产端为 stamping 发起跨聚合查询**。
2. **分发端 envelope-only**：matcher 与 handler 只读信封，永不反查业务域。
3. **快照真相**：属性记录的是生产时刻的归属。issue 后来挪了 epic，历史事件不改写。
4. **准入标准**：凡是值得作为路由维度的业务身份，就提升为信封属性；payload
   （`data`）永不参与路由。

### 命名

CloudEvents 扩展属性名限小写字母数字。约定：

- **用户可见身份用短名**：`issue`（issue number）。用户写表达式永远用人话身份。
- **内部 id 带 `id` 后缀**：`issueid`、`epicid`、`workflowrunid`、`agentid`、
  `sessionid`、`runnerid`、`projectid`。

### Stamping 矩阵

| 事件族 | projectid | epicid | issue / issueid | workflowrunid | agentid | sessionid | runnerid |
|---|---|---|---|---|---|---|---|
| `workflow.*` | ✅ | 如有 | 如有 | ✅ | – | – | – |
| `issue.*` | ✅ | 如有 | ✅ | – | – | – | – |
| `epic.*` | ✅ | ✅ | – | – | – | – | – |
| `agent-session.*` | ✅ | – | 如 Workflow 来源 | 如 Workflow 来源 | 如 Agent 来源 | ✅ | – |
| `runner.*` | 如有 | – | – | – | – | – | ✅ |

「如有」= 生产时该归属存在则必印，不存在则省略（不印空值）。

`workflow.stage.*`、`workflow.task.*`、`workflow.check.*` 事件另印 `stage`
（阶段名）——渲染占位符 `{{event.stage}}` 依赖它，不再从 `data` 解析。

`subject` 保留 CloudEvents 原义，不作为路由依据；issue 事件现有的
`subject = issue number` 维持兼容，不再扩展。

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
event.type == "com.mohist.issue.completed" && has(event.epicid)
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

两者共用同一 matcher 语义。**对称性即验收标准**：若某事件系统 handler 能路由到
而用户表达式订不到，即协议破损。

## Conformance

- `EventCatalog` 为每个 type 声明必印属性集合；
- 一组 spec 测试遍历所有事件生产路径，断言实际信封满足声明——新增事件忘印谱系
  时测试即红；
- 表达式求值器有独立 conformance 测试集（语法、缺失属性、正则超时、确定性）。

## 实装差距

当前代码与本协议的差距，由事件路由 epic 推进：

- `projectid` 已普遍印制；`issueid` 仅 workflow / issue 事件有；`issue`（number）、
  `epicid`、`workflowrunid` 等均未印。
- 订阅过滤为三个固定字段（Type 通配 + Source/Subject 精确），表达式未实装；
  `[Subscription]` 当前的 Type glob 语法见 [`eventbus.md`](eventbus.md) 的实装差距小节。
- `EventCatalog` 仍是纯常量表，无必印属性声明与 conformance 测试。
