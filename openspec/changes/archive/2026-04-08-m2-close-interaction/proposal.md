## Why

M2 "能交互" 里程碑有两项 backlog 遗留需要收尾：B-032（mo attach 连接协议）事实已决定为 SSE，需在 backlog 中标记关闭；B-033（ask_user 与自由文本冲突处理）需要实际解决——当前 `mo attach` 只能处理 gate 暂停，CLI 用户遇到 `ask_user` 阻塞时无法回答问题，只能等 24h 超时。

## What Changes

- **更新 backlog**: B-032 标记为已完成（SSE 已是实现事实）
- **mo attach 统一交互入口**: `mo attach` 同时感知 `agent_paused`（gate 暂停）和 `question_asked`（ask_user 阻塞）两种事件，用户输入自动路由到对应的 API
- **event-formatter 补全**: `question_asked` 和 `question_answered` 事件加入格式化配置
- **B-033 标记为已处理**: 通过 mo attach 统一入口解决冲突问题

## Capabilities

### New Capabilities

- `attach-unified-interaction`: mo attach 同时处理 gate 暂停和 ask_user 阻塞，智能路由用户输入

### Modified Capabilities

（无 spec 级别行为变更）

## Impact

- **CLI**: `attach.ts` 新增 question 交互模式，`event-formatter.ts` 新增 question 事件格式
- **API**: 无改动（`POST /questions/:id/reply` 和 `POST /issues/:number/messages` 已存在）
- **服务端**: 无改动
- **Backlog**: `prd/backlog/backlog.md` B-032、B-033 状态更新
