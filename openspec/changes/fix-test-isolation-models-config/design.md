## Context

`ModelsDev` 有三级数据源链（内存缓存 → 磁盘缓存 → 网络获取），全部使用进程级单例 `dataCache`。`ConfigLoader.load()` 直接读取 `~/.mohist/config.jsonc`。测试中没有 mock 这些模块，导致：

1. `resolve-model.test.ts` 的 "should select latest configured provider model" 测试：`resolveDefaultModel()` 调用 `ModelsDev.get()` → 读了真实的 `~/.mohist/cache/models.json` → anthropic 最新模型变成了 `zai-org/GLM-5.1` 而不是测试写时的 `claude-sonnet-4-6`
2. `api-routes.test.ts` 的 "should return llm.configured false when no llmConfig provided" 测试：`createStatusRoutes` 内部调用了 `resolveModel` 相关逻辑 → 读到真实的 `~/.mohist/config.jsonc` → `llm.configured = true`

## Goals / Non-Goals

**Goals:**

- `resolve-model.test.ts` 和 `api-routes.test.ts` 在任何环境（有/无 `~/.mohist/`）下都通过
- `ModelsDev` 提供测试友好的缓存控制接口
- 不修改生产代码的行为

**Non-Goals:**

- 不重构 `ModelsDev` 的缓存架构
- 不添加其他测试文件（只修复两个失败的测试）
- 不改动 `ConfigLoader` 的实现（只通过测试侧 mock 解决）

## Decisions

### Decision 1: 在 resolve-model.test.ts 中 vi.mock ModelsDev

**选择**: 用 `vi.mock` 在测试文件级别 mock 整个 `ModelsDev` 模块

**理由**: `resolveModel` 内部调用 `ModelsDev.get()`，该函数读取文件系统和网络。最干净的隔离方式是在测试文件中直接 mock 返回固定的模型数据。

**替代方案**:
- 给 `ModelsDev` 添加 `setTestData()` 方法 → 改生产代码，侵入性强
- 设置 `MODELS_DEV_DISABLE_FETCH` env var → 只阻止网络请求，不阻止读磁盘缓存

### Decision 2: 在 api-routes.test.ts 中 vi.mock resolveModel

**选择**: 在测试文件顶部用 `vi.mock('../src/agent-runtime', ...)` 让 `resolveModel` 默认 throw，从而保证 `llm.configured` 为 `false`

**理由**: 测试代码本身已经没传 `llmConfig` 给 `createStatusRoutes`，但 `status.ts` 内部会调用 `resolveModel(undefined)` → `resolveDefaultModel(undefined)` → `load()` 读取真实的 `~/.mohist/config.jsonc`。如果用户本地配置了带 apiKey 的 provider，`resolveModel` 不会 throw，导致 `llm.configured` 变成 `true`。mock `resolveModel` 是最彻底的隔离方式。

### Decision 3: 给 ModelsDev 增加 resetCache 导出

**选择**: 导出一个 `resetCache()` 函数，清除 `dataCache` 和 `dataCacheTime`

**理由**: 即使 mock 了 `ModelsDev.get()`，`afterEach` 中清理缓存仍然是好实践。而且 `resetCache` 对未来调试也有用。

## Risks / Trade-offs

**[Risk] mock 数据过时**: mock 的 `ModelsDev` 数据是硬编码的，如果 `resolveDefaultModel` 的排序逻辑改变，mock 数据需要同步更新。 → 缓解：mock 数据只需包含测试用到的 provider，且结构简单。特别要注意 `resolveDefaultModel` 遍历的是 **所有 builtin providers**，如果 mock 数据保留了其他 provider（如 zhipuai），而测试运行环境恰好设置了对应 env var（如 `ZHIPU_API_KEY`），它们仍可能竞争胜出。因此 mock 数据中应仅保留测试断言需要的 `anthropic` provider。

**[Risk] createStatusRoutes 的 llm 检查逻辑复杂**: 已确认它调用 `resolveModel(llmConfig)`，而 `undefined` 会回退到全局 `load()`。 → 缓解：通过 `vi.mock` 直接让 `resolveModel` 在测试里 throw，彻底切断对真实配置的依赖。

**[Pre-existing leak] models-dev.test.ts 也写真实文件**: 该测试中的 "should skip refresh when cache is fresh" 直接读写 `~/.mohist/cache/models.json`。本次 scope 不修复它，但应记录这一已知漏洞。 → 缓解：`resetCache()` 的引入为将来修复该测试提供了基础设施。

**[Cache interplay] `builtin-providers.ts` 也有进程级缓存**: `getBuiltinProviders()` 依赖 `asyncProvidersCache` / `asyncCachePromise`。未来若测试从 `vi.mock` 改为 `spyOn`，必须同时调用 `clearBuiltinProvidersCache()`。已在 `specs/test-isolation/spec.md` 中补充相关要求。
