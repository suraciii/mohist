## Context

Explore session 创建时标题硬编码为 `"New Exploration"`（出现在 `App.tsx:78` 和 `api/explore.ts:46` 两处）。当前没有 `updateTitle` 方法——`ExploreSessionRepo` 有 `updateModel`、`updateStatus`、`updateIssueId`、`crystallize`，唯独缺 title 更新。前端也没有 session 列表组件（`/explore` 路由由 `ExploreRedirect` 直接创建或跳转）。

关键约束：自动命名逻辑只能放在**后端 `POST /:id/messages` handler** 中，因为那是唯一知道"这是第一条消息"且拥有用户内容的地方。前端在创建 session 时不知道用户会输入什么。

## Goals / Non-Goals

**Goals:**
- Session 列表中的每个 session 有可区分的标题
- 用户可手动修改标题
- Crystallize 后标题反映 issue 内容

**Non-Goals:**
- 不做 LLM 生成标题（增加延迟和 token 成本，截取首条消息足够）
- 不做 session 列表页 UI（那是另一个 change 的范围）
- 不改数据库 schema（`title` 列已存在）

## Decisions

### D1: 自动命名放在后端 messages handler

在 `POST /:id/messages` handler 中，保存用户消息后检查 session 标题是否为默认值 `"New Exploration"` 且 messages 数量为 1（刚插入的这条），若是则截取用户消息前 50 字符更新标题。

不放在前端，因为前端 `useExploreStream.send()` 已经很复杂，不应再混入副作用逻辑。后端判断更准确（看 message count）。

**截取算法：** 取前 50 字符，若第 50 字符不是空格/换行则回退到最后一个空格处，加 `"..."` 后缀。短于 50 字符直接使用原文。

**Alternatives considered:**
- 前端在 `useExploreStream` 的 `onSuccess` 回调中调 PATCH：时序难控，stream 完成才触发
- LLM 生成标题：过度设计，首次消息本身就是最好的摘要

### D2: PATCH 端点统一处理手动编辑

新增 `PATCH /api/explore/:id`，只接受 `{ title: string }`。遵循现有 Hono 路由模式，参数校验与 `updateModel` 端点一致。

**Alternatives considered:**
- `PUT /api/explore/:id/title` 专用端点：过度细化，PATCH 语义更通用
- 用现有 `POST /:id/model` 的模式（POST + 专用子路径）：可扩展性差

### D3: Crystallize 标题更新放在后端

在 `POST /:id/crystallize` handler 中，成功创建 issue 后，从 `result` 中获取 issue 标题，调用 `exploreSessionRepo.updateTitle()`。只在新创建 issue 时更新（`!issueNumber` 分支），已有 issue 时不覆盖。

### D4: 前端双击编辑用 inline input 模式

在 `ExplorePage.tsx` 的标题 `<h2>` 上添加 `onDoubleClick` 处理，切换为 `<input>` 组件。使用 `useState` 管理 `isEditing` / `editValue` 状态，不引入额外依赖。

## Risks / Trade-offs

- [截取可能在多字节字符中间断开] → 使用 `Array.from()` 按码点截取而非 `String.slice()`
- [自动命名与手动编辑竞争] → 只在标题为默认值 `"New Exploration"` 时触发自动命名，用户编辑后不再覆盖
- [已有 20 个 "New Exploration" session 不会自动修复] → 可接受，用户手动编辑即可。不跑 migration 重命名历史数据

## Migration Plan

无数据库变更，无 breaking API 变更。部署后：
1. 新 session 自动获得有意义的标题
2. 已有 session 保持 `"New Exploration"` 直到用户编辑或被重新 crystallize
3. 回滚：删除 PATCH 端点和自动命名逻辑即可

## Open Questions

None.
