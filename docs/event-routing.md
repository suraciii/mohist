# Agent 事件路由

本文的 Agent 均指有稳定 ID、名称和 Instructions 的 **Mohist Agent（Named Agent）**，
不是 Workflow 直接调用的 Inline Agent。两者关系见 [Agent 与 AgentSession](agents.md)。

## 它解决什么问题

Mohist 是一条软件生产线。Workflow、issue、epic、runner、AgentSession 都会产生事件：
阶段等待审批、任务失败、issue 完成、epic 没有可推进的 issue、runner 掉线等。

事件路由让你把这些事件交给 Agent 响应。你在项目里维护一张**路由表**：每条规则
声明匹配什么事件、由哪个 Agent 响应、响应时用什么提示词。事件发生后，系统按表
匹配，命中的 Agent 自动启动，按提示词读取上下文并执行动作。

每次命中都会创建一次 AgentJob 和一段 AgentSession。AgentJob 记录这次响应是否完成，
AgentSession 记录对话和工具调用。

Agent 在这里是代理人。它进入流水线上原本由 owner 负责的位置：owner 能审批，
Agent 也能审批；owner 能分析失败、写总结、创建后续 issue，Agent 也通过同一套
动作完成这些事。Agent 不是特殊通道。判断逻辑归你的提示词，系统负责匹配事件、
启动 Agent、记录这次响应。

## 三个心智模型

### 1. 事件自带业务谱系

每个事件都带着一组**属性**，说明它发生在生产线的哪个位置：事件类型是什么
（`event.type`）、属于哪个 issue（`event.issue`）、哪个 epic、哪次 workflow 运行、
哪个阶段。凡是围绕某个 issue 发生的事件——无论出自 workflow 还是 issue 本身——
都带着这个 issue 的编号。

这意味着「盯住 issue #42 的一切」不需要任何特殊机制，一条表达式就够了。

### 2. 两层 Prompt

一个 Agent 实际行动时，它的指令由两层拼成：

- **第一层：Agent 内置指令**（定义「我是谁」）——配 Agent 时写一次，长期稳定，
  所有规则共享。比如「你是 owner 的代理人，负责审批 plan/check，review 要严谨，
  不确定就升级给 owner」。
- **第二层：规则的响应提示词**（定义「这次该做什么反应」）——配规则时写，
  每条规则各写各的。

**为什么拆开？** 同一个 Agent 可能要响应多种事件。把所有反应塞进身份指令会让
Agent 定义膨胀、难复用。**身份归 Agent，反应归规则**。

### 3. 路由表按序求值

规则是一张**有序表**，像邮件过滤器：事件来了从上往下逐条比对，命中就触发该条
规则的 Agent，然后**默认停止**。想让多个 Agent 响应同一事件，给上面的规则标
「继续」。

这一个模型同时表达三种需求：

- **独占响应**：审批这类只能有一个决策者的事件，天然 first-match；
- **并行响应**：issue 完成后一个 Agent 写 release note、另一个通知 owner——
  上面的规则标「继续」；
- **兜底 + 接管**：全局兜底规则放表底，针对某个 issue 的规则放它上面。
  谁先谁赢，一眼看懂，不用心算优先级。

## 表达式怎么写

规则用一条布尔表达式匹配事件属性，支持 `==`、`!=`、`&&`、`||`、`in`、
`startsWith` 等：

```
# 只盯 issue #42 的审批
event.type == "com.mohist.workflow.stage.approval-requested" && event.issue == "42"

# 全项目的 workflow 终态失败
event.type == "com.mohist.workflow.run.failed"

# issue #42 名下的一切事件
event.issue == "42"

# 某两个 issue 的完成事件
event.type == "com.mohist.issue.completed" && event.issue in ["42", "43"]
```

响应提示词里可以用同一套属性做占位符：`{{event.issue}}`、
`{{event.workflowrunid}}`、`{{event.stage}}`。Agent 拿到后自己去拉详情、做判断、
执行动作。

## 场景：让 Agent 监管一个 issue

核心场景：Agent 替你盯 issue 的推进——到达审批门它审批，终态失败它修，只有它
停手时才轮到你。内置预设 `mo agent install supervisor` 一条命令装好（Agent、
路由规则、提示词），行为细节见 [Agent 监管](agent-supervision.md)。

## 可见性：知道谁响应了什么

系统不做严格冲突拦截，但提供**可见性**让你核对配置是否符合预期：

- 从事件查：某次事件是被哪条规则、哪个 Agent 响应的；
- 从 AgentJob 查：这次执行是响应哪个事件、哪条规则触发的；
- **干跑**：拿最近的事件回放整张路由表，逐条显示会命中什么——配完规则先干跑，
  不用等真实事件来验证。

**配置正确性你负责，可观测性系统负责。**

## 更多场景

监管之外，同一张路由表还能表达：

| 场景 | 匹配的事件 | 响应提示词让 Agent 干什么 |
|---|---|---|
| 完成自动汇总 | issue 完成 | 汇总产物，写 release note |
| 后续工作生成 | issue 完成 / review 发现风险 | 创建 follow-up issue |
| 产线维护 | runner 掉线 / epic 无可推进 issue | 分析原因，通知 owner 或创建维护 issue |

这些规则共用同一张表、同一套表达式，只是匹配条件和响应提示词不同。

## 你和系统的责任边界

| 归谁 | 负责 |
|---|---|
| **你** | 把响应提示词写好、写安全；用表的顺序和「继续」表达独占、兜底或并行 |
| **系统** | 准确匹配事件、启动 Agent、记录事件与响应之间的关系 |
| **系统不负责** | 判断提示词对错；给 Agent 提供特殊审批通道。Agent 走的是和 owner、脚本一样的正规通道（详见[工作流详解](the-workflow.md)） |
