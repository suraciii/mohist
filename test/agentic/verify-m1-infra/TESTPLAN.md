# Test Plan: M1 Infrastructure (Layer A)

## Context

验证 mohist M1 基础设施层在干净容器中的端到端流程。

**范围**: server 启动、项目管理、issue CRUD、数据持久化、错误处理。
**不涉及**: LLM、agent、worktree、opencode。
**环境**: 容器中 server 已启动（entrypoint.sh），监听 `localhost:3456`。
**工作目录**: `/app/workspace`，数据目录: `/home/motest/.mohist/`。

## 执行方式

按 Phase 顺序执行。每个 Phase 用自然语言描述步骤和预期结果。
遇到复杂操作时，用 `@scripts/<name>.sh` 调用辅助脚本。

---

## Phase 1: Build Verification

确认构建产物完整，CLI 可用。

检查以下文件存在：
- `/opt/mohist-src/packages/cli/dist/server/index.js`
- `/opt/mohist-src/packages/cli/dist/cli/index.js`

运行 `which mo` 和 `which mo-server`，确认两者都在 PATH 中。

---

## Phase 2: Server Health

请求 `GET /api/health`，验证返回 JSON 中 `status` 为 `"ok"` 且包含 `timestamp` 字段。

---

## Phase 3: Project Management

1. 在 `/app/workspace/test-project` 创建目录并初始化 git 仓库
2. 运行 `mo project create test-project --path /app/workspace/test-project`，确认成功
3. 运行 `mo project list`，确认列表中包含 test-project
4. 运行 `mo project use test-project`，确认切换成功
5. 运行 `mo project show test-project`，确认显示正确的名称和路径

---

## Phase 4: Issue CRUD

### 4.1 创建 issue

- `mo issue create "Add hello function" --body "Create hello.ts with greet"` → 确认创建 #1
- `mo issue create "Add goodbye function" -l enhancement` → 确认创建 #2
- 两个 issue 的 stage 都应为 draft

### 4.2 列表与过滤

- `mo issue list` → 显示 2 个 issue
- `mo issue list -s draft` → 返回 2 个
- `mo issue list -l enhancement` → 只返回 #2，不包含 #1

### 4.3 详情与更新

- `mo issue show 1` → 确认标题 "Add hello function"、body、stage=draft
- `mo issue update 1 --body "Create hello.ts with greet() returning string"` → 确认更新成功
- `mo issue comment 1 "Should also export the function"` → 确认添加成功
- 再次 `mo issue show 1` → 新 body 和评论都可见

### 4.4 标签

- `mo issue update 1 -l +feature -l +priority` → 添加标签
- `mo issue show 1` → 确认 feature 和 priority 标签可见

### 4.5 关闭与重开

- `mo issue close 2` → 确认关闭成功，status 应为 blocked
- `mo issue reopen 2` → 确认重新打开成功

---

## Phase 5: Data Persistence

验证 server 重启后数据不丢失。

1. 记录当前 issue 数量：`GET /api/issues`，`.data` 长度应为 2
2. 调用 `@scripts/restart-server.sh` 重启 server
3. 运行 `mo project use test-project` 恢复上下文
4. 验证数据完整：
   - `GET /api/issues` → `.data` 长度仍为 2
   - `mo issue show 1` → 更新的 body（含 "returning string"）、评论（含 "Should also export"）、标签（feature、priority）都还在

---

## Phase 6: Error Handling

每个操作都应返回错误信息，不崩溃。逐一测试：

- `mo issue show 999` → 应返回 not found 错误
- `mo project use nonexistent-project` → 应返回错误
- 无当前 project 时，`POST /api/issues` body `{"title":"orphan"}` → 应失败
- 先 close #2，再 `POST /api/issues/2/start` → 应被拒绝
- `mo project create test-project --path /app/workspace/test-project` → 应报 already exists
- 最后 `mo issue reopen 2` 恢复 #2 状态

---

## Phase 7: API Response Structure

直接检查 HTTP API 的 JSON 响应结构。

- `GET /api/issues` → `.success` 为 true，`.data` 长度 2，每个 issue 包含 id / number / title / stage / status / labels 字段
- `GET /api/issues/1` → `.data.stage` 为 "draft"，`.data.comments` 长度 1
- `GET /api/labels` → 返回的 labels 包含 enhancement 和 feature
- `GET /api/config` → `.success` 为 true

---

## 收集结果

所有 Phase 执行完毕后，汇报每个 Phase 的通过/失败状态。7 个 Phase 全部通过则测试通过。
