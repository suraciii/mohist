## MODIFIED Requirements

### Requirement: REQ-4: Test Isolation

每个测试套件 MUST 有完整的数据库和文件系统隔离。

- 测试 SHALL 通过 `new DatabaseManager({ inMemory: true })` 创建自己的 DatabaseManager 实例
- 使用 StateManager 的测试 SHALL 通过 `new StateManager(db)` 创建，使用自己的 db 实例
- 直接使用 repo 的测试 SHALL 自行调用 `initializeDatabase(db)`
- 测试 SHALL NOT 与其他测试共享数据库连接
- 测试 SHALL 在 `afterEach` 块中通过 `db.close()` 清理
- 测试 SHALL NOT 读取真实文件系统中的配置文件（`~/.mohist/config.jsonc` 等）
- 测试 SHALL NOT 读取真实文件系统中的缓存文件（`~/.mohist/cache/models.json` 等）
- 使用 `resolveModel` 的测试 SHALL mock `ModelsDev.get()` 返回固定数据
- 使用真实 `ModelsDev` 的测试 SHALL 在 `afterEach` 中调用 `ModelsDev.resetCache()`
- 使用真实 `getBuiltinProviders()` 的测试 SHALL 在 `afterEach` 中调用 `clearBuiltinProvidersCache()`
- 使用 `createStatusRoutes` 的测试 SHALL mock `resolveModel` 或显式控制 `llmConfig`，切断对全局配置的依赖

#### Scenario: 测试不读取真实配置文件

- **WHEN** 测试代码运行
- **THEN** 测试 SHALL NOT 触发对 `~/.mohist/config.jsonc` 的读取
- **AND** 测试 SHALL NOT 触发对 `~/.mohist/cache/models.json` 的读取
- **AND** 测试结果与 `~/.mohist/` 目录的内容无关

#### Scenario: resolve-model 测试环境无关

- **WHEN** 删除 `~/.mohist/cache/models.json` 后运行 `resolve-model.test.ts`
- **AND** `~/.mohist/config.jsonc` 不存在
- **THEN** 所有测试仍然通过，结果一致

#### Scenario: api-routes 测试环境无关

- **WHEN** 删除 `~/.mohist/config.jsonc` 后运行 `api-routes.test.ts`
- **THEN** "should return llm.configured false when no llmConfig provided" 测试通过
- **AND** 测试结果与本地配置文件内容无关
