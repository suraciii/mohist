## Why

两个测试（`resolve-model.test.ts` 和 `api-routes.test.ts`）在本地环境失败但在 CI 可能通过，原因是 `ModelsDev` 和 `ConfigLoader` 读取了真实的文件系统（`~/.mohist/cache/models.json`、`~/.mohist/config.jsonc`、`models-snapshot.js`）。这两个模块都有进程级单例缓存，第一个测试加载的数据会"感染"后续测试，导致测试结果依赖运行环境。

## What Changes

- **`resolve-model.test.ts`**：mock `ModelsDev.get()` 返回固定的 provider/model 数据，不再依赖真实文件或网络
- **`api-routes.test.ts`**：mock 或注入 `llmConfig`，避免 `createStatusRoutes` 读到真实配置
- **`ModelsDev`**：增加 `resetCache()` 方法供测试清理进程级单例缓存
- **`ConfigLoader`**：确认测试中不调用 `load()` 读取真实文件，必要时 mock

## Capabilities

### New Capabilities

- `models-dev-testability`: `ModelsDev` 模块支持测试环境下的缓存重置和 mock 注入

### Modified Capabilities

- `test-isolation`: REQ-4 补充要求——测试 SHALL NOT 读取真实文件系统中的 models.json、config.jsonc 等，SHALL mock 所有文件 I/O

## Impact

- `packages/cli/src/config/models-dev.ts`：增加 `resetCache()` 导出
- `packages/cli/tests/resolve-model.test.ts`：添加 `ModelsDev` mock
- `packages/cli/tests/api-routes.test.ts`：修复 `llm.configured` 测试的隔离性
- 不影响任何生产代码路径
