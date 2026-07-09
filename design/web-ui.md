# Web UI

Web UI 是 Mohist 的本地管理界面——让用户观察 issue/workflow 状态，并执行审批、启动、暂停等用户动作。

## 边界

| 职责 | 归属 |
|------|------|
| 渲染 issue/workflow 状态 | Web UI |
| 用户动作提交 | Web UI → API |
| authoritative state | Server |
| workflow 状态裁判 | WorkflowGrain |
| 执行（shell/agent/git） | Runner |
| 实时观察推送 | Server events → Web UI |

Web UI 不解释 workflow 规则，只展示 server state 并把用户意图提交给 API。

## 事件模型

实时事件是观察，不是驱动。Server 通过 SignalR（`/hubs/events`）推送事件到 Web UI，UI 据此 invalidate 或 patch 查询。UI 断线重连后自己对账——系统不依赖 UI 消费事件来推进 workflow。

```text
WorkflowGrain 提交事件
  → server 持久化/发布
  → SignalR hub 转发到 Web UI
  → Web UI 刷新查询
```

## 资源身份

UI 路由可用用户友好的路径（如 `/projects/{projectId}/issues/{number}`）。API/查询边界把 display number 解析为 `issueId`。内部调用和事件 subject 优先用 `issueId` / `workflowRunId`。见 [`conventions.md`](conventions.md)。

## 放置规则

- Query hooks 拥有数据获取与缓存失效。
- 组件渲染状态、收集用户意图。
- UI state 可记住视图偏好、筛选、选中、草稿——不可成为 workflow truth 来源。
- Runner 细节留在 API payload 后；UI 不依赖 process 实现细节。

## 设计偏好

Mohist 是运维工具，偏好密集、可扫描的屏幕，避免营销式留白页。

首屏：issue list/board → workflow run detail → approval queue → runner status。

不设独立应用内落地页。
