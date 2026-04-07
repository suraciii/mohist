## Why

Explore-mode 实现已完成全部 9 个任务，但代码审查发现 14 个问题：4 个严重可见 bug、4 个高优先级问题、6 个中低优先级问题。需要修复这些问题以确保功能可用性和代码质量。

## What Changes

- 修复 Issue ID 类型不匹配：后端发送 `issue.number`（数字）而非 `issue.id`（UUID），使 "View Issue" 链接正常工作
- 修复用户消息过早保存：改为 agent 成功后再保存用户消息，避免失败时产生孤立/重复消息
- 修复路径遍历安全漏洞：`read-file.ts` 的 `startsWith` 检查增加路径分隔符边界
- 修复前端 Markdown 无样式：安装 `@tailwindcss/typography` 插件
- 修复前端流错误静默吞掉：在 `useExploreStream` 中暴露 error 状态，UI 显示错误提示
- 修复 `grep-tool.ts` 缺少 try/catch：与 `glob-tool.ts` 保持一致的权限错误处理
- 修复 `ExploreRedirect` 无错误处理：session 创建失败时显示错误而非无限 loading
- `create-issue-tool.ts` 改为通过 context 注入 `eventBus`
- `explore-agent.ts` 修复 `any` 返回类型
- `explore-service.ts:addMessage` 增加 session 存在性校验
- 清理 `explore-session-repo.ts` 死代码 `touch()` 方法
- `createdIssueId` 改为从 tool result 直接捕获

## Capabilities

### New Capabilities

（无新能力）

### Modified Capabilities

- `http-api`: 修复 explore messages 端点的消息持久化时序和错误处理
- `web-ui`: 修复 markdown 样式、错误展示、issue 导航链接

## Impact

- **后端**: `tools/read-file.ts`, `tools/grep-tool.ts`, `tools/create-issue-tool.ts`, `agents/explore-agent.ts`, `services/explore-service.ts`, `db/explore-session-repo.ts`, `api/explore.ts`
- **前端**: `package.json`（新增依赖）, `hooks/useExploreStream.ts`, `components/ExploreChat.tsx`, `App.tsx`
- **依赖**: 新增 `@tailwindcss/typography` 前端依赖
