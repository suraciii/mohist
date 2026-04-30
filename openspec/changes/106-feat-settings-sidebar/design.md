## Context

Settings 页面当前使用 2 个 top tabs（Providers / General），由 `SettingsPage.tsx`（432 行）和 `GeneralSettingsSection.tsx`（242 行）组成。配置分散在两套系统中：JSONC 文件（`~/.mohist/config.jsonc`，Zod schema 校验）存储 provider/model/server/agent 结构化配置，SQLite `ConfigService` 存储运行时配置（`agent.timeout`、`agent.maxConcurrent`、`poll.interval`）。前端通过 `useConfig` / `useUpdateConfig` hooks 操作 SQLite 配置，通过 `providerApi` 操作 JSONC 配置。

后端路由通过 `HttpServer.addRouter()` 挂载 Hono 子路由。已有 `/api/config`（GET/PUT 单 key）、`/api/providers`（完整 CRUD）、`/api/opencode/models`（模型列表）等端点。

JSONC config schema 已支持 `model`、`opencode.model`、`opencode.stageModels`、`agent.timeout`、`agent.maxConcurrent`、`log.level` 等字段，但前端无对应 UI。

## Goals / Non-Goals

**Goals:**
- 将 Settings 页重构为 sidebar 导航 + 3 个 section（AI / Agent / System）
- 补齐所有缺失配置项的 UI（model 选择、stage/task timeout、recovery、log level、system info）
- Agent section 使用 section 级 Save + dirty state，替代 per-field Save
- 新增后端 API 端点供新 UI 消费
- URL 从 `?tab=` 改为 `/settings/:section` path param

**Non-Goals:**
- 不做交互式 timeout 树或可视化图表
- 不做 server host/port 可编辑字段
- 不做 Workflow 或 Skills 管理 UI
- 不重构后端 config 存储层（保持 JSONC + SQLite 双系统，后续统一）

## Decisions

### D1: 前端路由方案 — React Router path param

使用 React Router 的 `<Route path="/settings/:section">` 匹配 section。`/settings` 路径用 `<Navigate>` 重定向到 `/settings/ai`。SettingsPage 组件内部用 `useParams()` 读取 section 并渲染对应 section 组件。

**Alternatives considered:**
- 状态管理（useState + 无 URL 同步）：不支持 deep linking，放弃
- Query param（`?section=ai`）：URL 不够 clean，不利于未来扩展

### D2: Section 组件文件组织

拆分为 4 个新组件文件，替代现有 2 个文件：

| 文件 | 职责 |
|------|------|
| `SettingsPage.tsx` | 重写为布局容器：sidebar + 内容区 + mobile dropdown |
| `AiSettingsSection.tsx` | 新建：Provider 统一列表 + Custom Providers + Model Selection |
| `AgentSettingsSection.tsx` | 新建：Timeouts + Concurrency + Recovery + section Save |
| `SystemSettingsSection.tsx` | 新建：Log Level + About 只读信息 |

不再保留 `GeneralSettingsSection.tsx`（原内容拆分到 Agent + System）。`ProviderConnectDialog.tsx` 和 `CustomProviderDialog.tsx` 保持不变。

**Alternatives considered:**
- 单一 SettingsPage.tsx 包含所有逻辑：文件过大，放弃
- 每个组件一个目录（index.tsx + styles）：当前项目无此模式，不引入

### D3: Agent section dirty state 追踪 — 本地 state 比对

AgentSettingsSection 内部维护 `localValues` state（从 API 数据初始化），与 `savedValues` 比对判断 dirty。dirty 时 "Save Changes" 高亮可点击。点击 Save 调用批量 API 一次提交所有变更字段。

**Alternatives considered:**
- `react-hook-form`：引入新依赖，当前项目无此库
- Context 级 dirty tracking：只有 Agent section 需要，过度设计

### D4: 后端 API 策略 — 新增专用端点 + 批量更新

新增 5 个 API 端点：

| 端点 | 方法 | 用途 | 存储层 |
|------|------|------|--------|
| `/api/system/info` | GET | 版本、路径、server 状态 | 运行时计算 |
| `/api/config/model` | GET/PUT | mohist model | JSONC `config.model` |
| `/api/config/opencode-model` | GET/PUT | coder model | JSONC `config.opencode.model` |
| `/api/config/log-level` | GET/PUT | log level | JSONC `config.log.level` |
| `/api/config/agent-runtime` | PUT | 批量更新所有 agent 配置 | JSONC `config.agent.*` + SQLite |

新端点直接操作 JSONC 文件（通过 `config-loader.ts` 的 `load()`/`writeConfig()`），与 SQLite `ConfigService` 并存。`/api/config/agent-runtime` 批量端点将 timeout/maxConcurrent 写入 JSONC（`config.agent.timeout`、`config.agent.maxConcurrent`），同时保留 SQLite 兼容。后续迭代可统一到 JSONC。

**不修改现有 `PUT /api/config/:key` 端点**，保持向后兼容。

**Alternatives considered:**
- 扩展现有 `PUT /api/config/:key` 支持嵌套 key：key 格式歧义（`agent.timeout` 是 SQLite key 还是 JSONC path？），放弃
- 统一所有配置到 JSONC，移除 SQLite ConfigService：影响范围过大，非本次目标

### D5: Provider 统一列表 — 前端排序

复用现有 `GET /api/providers` 返回的数据（`Provider.configured` 区分已连接/未连接），前端合并为单列表并排序：`configured` 为 true 的排前面。不新增后端排序参数。

### D6: System info 端点 — 运行时计算

`GET /api/system/info` 不缓存，每次请求实时计算。version 从 `package.json` 读取（`require('../../package.json').version`），git hash 从 `child_process.execSync('git rev-parse --short HEAD')` 读取（失败时返回 "unknown"），opencode bin path 从 `which opencode` 或 `config.opencode.binPath` 获取。

### D7: Timeout 解释性图表 — 纯 JSX 文本

使用 `<pre>` 或 `<code>` 块渲染静态 ASCII 树，数值部分用模板字符串从 state 读取，实现动态更新。不引入任何图表库。

## Risks / Trade-offs

**[JSONC + SQLite 双配置源]** → Agent section 的 timeout/maxConcurrent 当前存在两份（SQLite `ConfigService` 和 JSONC `config.agent.*`）。新 UI 写 JSONC，旧 `GET /api/config` 读 SQLite。`/api/config/agent-runtime` 端点需要同时写两边以保证兼容。后续需统一。

**[writeConfig 并发冲突]** → `writeConfig` 使用 `_version` 乐观并发控制。如果两个 tab 同时修改配置，后者会收到 `ConfigConflictError`。前端需处理此错误，提示用户刷新重试。

**[git hash 获取不稳定]** → 生产环境可能不在 git 仓库中运行。fallback 为 "unknown"，前端需处理此情况。

**[120+ provider 列表性能]** → `GET /api/providers` 返回 120+ 个 provider。统一列表需要前端渲染所有项。用搜索过滤 + React.memo 优化。如果性能不够，后续加虚拟滚动。

## Migration Plan

1. **新增后端 API**：先实现 5 个新端点，可通过 curl 测试
2. **重写前端**：替换 SettingsPage + 拆分 3 个 section 组件
3. **更新路由**：App.tsx 注册 `/settings/:section`
4. **移除旧代码**：删除 GeneralSettingsSection.tsx、SettingsPage 中的 Tab/TabPanel/ConnectedProvidersList/AvailableProvidersList 组件
5. **无数据库迁移**：纯前端 + API 变更，无 schema 变化

回滚：git revert 即可，无破坏性变更。

## Open Questions

- `config.agent.stageTimeout` 和 `config.agent.taskTimeout` 在 JSONC schema 中尚未定义。需要先扩展 schema 还是放在 opencode 嵌套配置中？— 建议：扩展 `agent` 对象增加 `stageTimeout` 和 `taskTimeout` 字段，同时增加 `maxGracePeriods`。
- Agent section 的 `pollInterval` 当前存为 SQLite key `poll.interval`，不在 JSONC `agent` 对象中。是迁移到 JSONC 还是保持 SQLite？— 建议迁移到 JSONC `agent.pollInterval`，批量端点统一写入。
