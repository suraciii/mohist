## Tasks

### Phase 1: 清理 Issue Provider

- [x] **T1.1** 删除 `packages/cli/src/providers/` 目录
  - 删除 `interface.ts`
  - 删除 `local.ts`
  - 删除 `index.ts`
  - 更新 `packages/cli/src/services/index.ts`（如果有导出）
  - 更新 `packages/cli/src/index.ts`（如果有导出）

### Phase 2: 配置系统基础

- [x] **T2.1** 创建 `packages/cli/src/config/` 目录结构
  - `config-loader.ts` — 加载和解析 `~/.mohist/config.json`
  - `config-schema.ts` — TypeScript 类型定义
  - `index.ts` — 统一导出

- [x] **T2.2** 实现配置加载器
  - 读取 `~/.mohist/config.json`
  - 解析 `${env:VAR}` 语法
  - 类型校验（Zod 或 TypeScript）
  - 处理文件不存在的情况（使用空配置）

- [x] **T2.3** 创建默认配置文件
  - Server 启动时检查 `~/.mohist/config.json` 是否存在
  - 不存在则创建默认文件（带注释说明）

### Phase 3: models.dev 集成

- [ ] **T3.1** 创建 `packages/cli/src/config/models-cache.ts`
  - `fetchModels()` — 从 models.dev 拉取数据
  - `readCache()` — 读取本地缓存
  - `writeCache()` — 写入本地缓存
  - `shouldRefresh()` — 检查是否需要刷新（TTL=5min）
  - 文件锁防止并发写入

- [ ] **T3.2** 实现自动同步
  - Server 启动时自动同步（如果缓存过期或不存在）
  - 后台每小时检查一次
  - 支持 `force` 参数强制刷新

- [ ] **T3.3** 离线支持
  - 缓存文件格式与 models.dev 一致
  - 无网络时使用缓存（即使过期）
  - 完全无缓存时提供最小内置配置

### Phase 4: Provider Registry

- [x] **T4.1** 重写 `packages/cli/src/agent-runtime/llm.ts`
  - 删除硬编码的 `PROVIDER_ENV` 和 switch-case
  - 实现动态 provider 解析
  - 根据 `model.api.npm` 选择 SDK 类型
  - 从 config.json 获取 apiKey
  - 从 models-cache 获取 baseURL

- [x] **T4.2** 实现三类 SDK 创建
  - `createAnthropicProvider()`
  - `createOpenAIProvider()`
  - `createOpenAICompatibleProvider()` — 用于所有国产模型

- [x] **T4.3** 更新 Server 初始化
  - `packages/cli/src/server/index.ts`
  - 初始化 ConfigLoader
  - 同步 models.dev 数据
  - 将配置传递给 agent-runtime

### Phase 5: CLI 命令

- [x] **T5.1** 实现 `mo models list`
  - 读取 models-cache
  - 按 provider 分组显示模型
  - 显示模型名称、ID、上下文窗口、是否支持工具调用
  - 标记当前配置的默认模型

- [x] **T5.2** 实现 `mo models sync`
  - 强制从 models.dev 拉取最新数据
  - 更新本地缓存
  - 显示同步结果（新增/更新/删除的 provider 和模型）

- [x] **T5.3** 注册 CLI 命令
  - 更新 `packages/cli/src/cli/index.ts`
  - 添加 `models` 子命令组

### Phase 6: 数据迁移和弃用

- [x] **T6.1** 标记 SQLite `llm.*` 配置为弃用
  - Server 启动时检测 SQLite 中的 `llm.model`、`llm.provider.*` 键
  - 打印迁移提示（引导用户手动迁移到 config.json）
  - 优先使用 config.json，忽略 SQLite llm 配置

- [x] **T6.2** 更新文档
  - README.md — 新增配置说明
  - AGENTS.md — 如果有相关说明
  - 提供 config.json 示例

### Phase 7: 测试

- [x] **T7.1** 单元测试
  - 配置加载器（正常、缺失文件、语法错误）
  - models-cache（同步、缓存、TTL）
  - provider 解析（三类 SDK）

- [x] **T7.2** 集成测试
  - Server 启动流程
  - CLI 命令（list、sync）

- [x] **T7.3** 手动测试
  - anthropic（原生 SDK）
  - openai（原生 SDK）
  - zhipu/glm-4（openai-compatible）
  - moonshot/kimi-k2（openai-compatible）
  - minimax（openai-compatible）

## Verification

完成所有任务后验证：

1. ✅ `src/providers/` 目录已删除
2. ✅ `~/.mohist/config.json` 可以配置多个 provider 的 apiKey
3. ✅ `mo models list` 显示所有可用模型（包括国产模型）
4. ✅ `mo models sync` 成功刷新缓存
5. ✅ Server 可以正常使用 anthropic、openai、zhipu、moonshot、minimax
6. ✅ 缺失 apiKey 的 provider 被正确跳过
7. ✅ SQLite 中的 `llm.*` 配置不再生效（但 server.port 等继续工作）
