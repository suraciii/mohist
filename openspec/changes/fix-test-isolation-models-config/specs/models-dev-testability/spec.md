## ADDED Requirements

### Requirement: ModelsDev 支持测试环境缓存重置

`ModelsDev` 模块 SHALL 导出 `resetCache()` 函数，用于清除进程级内存缓存。

#### Scenario: 调用 resetCache 清除内存缓存

- **WHEN** 测试代码调用 `ModelsDev.resetCache()`
- **THEN** `ModelsDev` 的内部内存缓存（`dataCache`）被清空
- **AND** 缓存时间戳（`dataCacheTime`）被重置为 0
- **AND** 下次调用 `ModelsDev.get()` 会重新从数据源加载数据

### Requirement: 测试通过 vi.mock 隔离 ModelsDev

使用 `resolveModel` 的测试 SHALL 通过 `vi.mock` mock `ModelsDev` 模块，返回固定的模型数据，不依赖文件系统或网络。

#### Scenario: resolve-model 测试 mock ModelsDev

- **WHEN** `resolve-model.test.ts` 运行
- **THEN** `ModelsDev.get()` 被 mock 返回固定的 provider 模型数据
- **AND** 测试不读取 `~/.mohist/cache/models.json`
- **AND** 测试不读取 `models-snapshot.js`
- **AND** 测试不发起网络请求到 `https://models.dev`
- **AND** 测试结果在任何环境下一致
