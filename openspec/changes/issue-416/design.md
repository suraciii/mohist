## Context

当前 Mohist 的 Project 已经存储 `RepositoriesJson` 并解析为多仓库列表（`ProjectInfo.Repositories` / `RepositoryInfo`），但业务规则并不完整：

- `ProjectGrain.CreateAsync` 仍创建空仓库列表，导致 Project 创建后没有可用执行资源。
- `RemoveRepositoryAsync` 在删除 default 仓库时会静默提升下一个仓库，破坏可预期性。
- `UpdateRepositoryAsync` 允许重命名和修改 `IsDefault`，使资源名作为稳定引用句柄的语义变弱。
- 现有 Project 数据需要升级到"恰好一个 default"不变式。
- CLI 的 `project create` 仍接受无 `--path` 的调用，而 `repo update` 仍暴露 `--new-name`/`--set-default`。

本 issue 在已有仓库资源模型的基础上，完成 Project 对仓库资源的所有权、生命周期、default 不变式、CLI 命令面以及既有数据迁移，使 Project 成为跨多个代码库的产品工作空间，同时为后续 issue 的目标仓库绑定提供稳定的引用模型。

## Goals / Non-Goals

**Goals:**

- 每个 Project 拥有至少一个命名仓库资源，资源名在 Project 内唯一（不区分大小写），恰好一个是 default。
- 仓库资源具备完整的生命周期：add、update（只改 Git URL / base branch）、set-default、delete（非 default 可删）。
- Project 创建必须原子地带入一个初始仓库，并设为 default；`mo project create --path <path>` 从本地 Git 仓库解析元数据。
- 既有 Project 的仓库数据升级到 default 不变式，保留原有名称、Git URL、base branch、顺序，不破坏已有 issue 的执行。
- 完成 `mo repo` 命令组及其错误输出、table/json 渲染。
- 保持 Runner 协议不变：issue 未指定仓库时仍使用 Project default 的 Git URL 和 base branch。

**Non-Goals:**

- issue 的目标仓库绑定与执行分流（后续 issue）。
- 跨 Project 共享仓库声明。
- 多仓库协同发布。
- 为 Web UI 新增仓库管理界面（只要求 API 消费者能兼容新契约）。

## Decisions

### 1. 复用并收紧现有 `ProjectInfo.Repositories` 模型

现有 `ProjectInfo` 已包含 `List<RepositoryInfo>`，`RepositoryInfo` 已包含 `Name`、`GitUrl`、`BaseBranch`、`IsDefault`。不再引入新的数据库列或聚合根，只收紧规则。

- **Rationale**: 减少数据迁移量；已有持久化格式 `RepositoriesJson` 与后续目标仓库引用直接对应；Orleans grain 已围绕该模型实现。
- **Alternatives considered**: 把仓库拆成独立表/聚合根。 rejected：本 issue 范围是 Project 子域内的资源声明，无需跨 Project 共享，独立表会增加 Join 与事务复杂度。

### 2. 将"恰好一个 default"作为 grain 层的聚合不变式，而非仅 API 校验

`ProjectGrain` 在每次变更后都确保列表中恰好一个 `IsDefault = true`：add 时按需切换、set-default 时原子重置、update 不改变 default、delete 不允许删除 default。`ProjectInfo` 在反序列化/生成时做防御性校验，API/CLI 只负责转发与报错。

- **Rationale**: 聚合根是 Orleans grain，其状态是事实来源；把不变式放在 grain 中可保证所有调用路径（API、内部测试、未来其他 grain）一致。
- **Alternatives considered**: 在 API 层或数据库层做校验。 rejected：API 层易被绕过，数据库层无法表达业务错误语义。

### 3. Project 创建改为 repository-backed 原子操作

`ProjectGrain.CreateAsync` 接收初始仓库元数据（name, gitUrl, baseBranch），在创建 Project 行时一并写入 `RepositoriesJson`，并标记为 default。API 与 CLI 都拒绝无初始仓库的创建请求。

- **Rationale**: 保证"每个可用 Project 至少有一个仓库"这条不变式从创建时就成立，避免后续分支处理。
- **Alternatives considered**: 先创建空 Project，再调用 add repository。 rejected：两步操作会在中间状态暴露无仓库的 Project，且需要补偿逻辑。

### 4. 删除 default 仓库时直接拒绝，而不是自动提升

`RemoveRepositoryAsync` 检查目标仓库 `IsDefault`，若是则抛出/返回冲突错误，提示先 `set-default`；不再执行任何静默提升。

- **Rationale**: 提案明确这是 BREAKING 行为；自动提升会隐藏用户意图，且可能把错误仓库变成执行入口。
- **Alternatives considered**: 删除 default 时如果还有其他仓库，按声明顺序提升第一个。 rejected：与提案冲突，且会让命令成功语义不可预期。

### 5. `update` 只更新元数据，重命名和切换 default 走专门命令

`UpdateRepositoryAsync` 不再接受 `newName` 和 `isDefault` 参数；API 的 `UpdateRepositoryRequest` 只保留 `GitUrl` 和 `BaseBranch`。CLI `repo update` 拒绝 `--new-name` 和 `--set-default`。`repo set-default` 是切换 default 的唯一入口。

- **Rationale**: 资源名是后续 issue 中 issue 目标仓库绑定的稳定引用；让 update 同时改身份和 default 会造成命令语义重叠和误操作风险。
- **Alternatives considered**: 保留 `newName` 但限制使用场景。 rejected：任何重命名都会破坏稳定引用，不如直接禁止。

### 6. 数据迁移采用一次性 startup migration + 读取时防御性校验

通过 EF Core migration 升级 schema 版本（如需要），并在应用启动时读取 `Projects.RepositoriesJson`，对每个 Project 执行：

1. 解析 JSON 为 `List<RepositoryInfo>`；
2. 校验每个仓库有非空 name、gitUrl、baseBranch；
3. 校验 name 不区分大小写唯一；
4. 按以下规则确定 default：
   - 已标记 default 的取第一个；
   - 无 default 时取声明顺序第一个；
   - 多个 default 时保留第一个标记的，其余设为非 default；
5. 将修正后的 JSON 写回数据库。

若某 Project 数据无法恢复（空列表、缺少必填字段、大小写冲突），则记录可操作的诊断并停止启动，不自动修改数据。

- **Rationale**: 一次性纠正所有历史数据；失败即停避免部分升级导致不一致；保留顺序和元数据确保现有 issue 可继续执行。
- **Alternatives considered**: 在首次访问每个 Project 时惰性修复。 rejected：启动时校验更可控，便于运行集成测试和迁移 spec；失败早暴露比运行中突然发现更安全。

### 7. CLI `project create --path` 在本地解析 Git 元数据

CLI 读取给定路径的 `.git` 目录，解析 remote origin URL 与当前 HEAD 分支，作为初始仓库的 `gitUrl` 和 `baseBranch`。资源名优先从仓库目录名或 remote 仓库名推导，要求非空。路径本身不发送到 server，也不持久化。

- **Rationale**: 保持单仓库场景"一条命令起步"的体验，同时确保 server 只保存声明式资源模型。
- **Alternatives considered**: 把本地路径发到 server 让 server 解析。 rejected：server 不应访问 CLI 本地路径，且 runner 需要的是 Git URL；把解析放在 CLI 也避免 server 引入 Git 依赖。

### 8. 错误模型保持 actionable，冲突/不存在/未解析 Project 都直接返回

Server 使用带类型的结果或异常码（如 `RepositoryConflictError`、`RepositoryNotFoundError`、`ProjectNotFoundError`），CLI 将其渲染为可读消息并明确下一步操作（如 `mo repo set-default <other>`）。

- **Rationale**: specs 对错误输出有明确验收标准；actionable 错误降低用户误操作成本。
- **Alternatives considered**: 统一返回 400 不区分错误类型。 rejected：CLI 无法给出精准提示，且不符合验收标准。

## Risks / Trade-offs

- **[Risk] 数据迁移失败会阻塞应用启动。** -> Mitigation: 迁移逻辑在独立 migration 或 startup task 中执行，并在失败时输出具体 Project ID 和原因，运维人员可手动修复数据后重启；集成测试覆盖空数据、多 default、大小写冲突等场景。
- **[Risk] 删除 default 由"静默提升"改为"拒绝"是 BREAKING，可能影响脚本或用户习惯。** -> Mitigation: 这是提案明确要求的破坏性变更；CLI 错误提示直接给出 `mo repo set-default <name>` 命令，降低学习成本。
- **[Risk] `project create --path` 要求本地路径是有效 Git 仓库，可能让 CI/自动化场景多一步准备。** -> Mitigation: 这是"仓库资源模型"落地的必要前提；后续仍可通过 API 直接传入 name/gitUrl/baseBranch 创建 Project，CLI 只是其中一种入口。
- **[Risk] 大小写不敏感的资源名在跨平台存储时可能产生歧义。** -> Mitigation: 在 grain 和 CLI 层统一使用 `StringComparer.OrdinalIgnoreCase` 做 name 校验；返回资源名时保留用户原始输入。
- **[Risk] Runner 与 issue 启动仍使用 default 仓库，若迁移后 default 选择规则与管理员预期不同，会影响现有 issue。** -> Mitigation: 迁移规则确定性地取"第一个声明"或"第一个已标记 default"，与现有单仓库场景完全一致；多仓库旧数据是 edge case，可通过迁移诊断提前发现。

## Migration Plan

1. **Schema & code migration**: 新增 EF Core migration（或 startup data migration）并在 `IHostApplicationLifetime` 启动早期执行仓库数据升级。
2. **Deployment**: 部署新版 server；启动时自动读取所有 Project 的 `RepositoriesJson`，校验并规范化 default；成功后服务才接收流量。
3. **Rollback**: 若迁移失败，启动停止，旧版本 server 仍可运行；回滚只需重新部署旧版本。迁移脚本只读取和重写 `RepositoriesJson`，不修改 schema 列或 issue 数据，因此回滚风险低。
4. **CLI rollout**: 同步发布新版 CLI，使 `mo project create` 要求 `--path`。旧 CLI 仍可调用 API，但 server 会拒绝无初始仓库的请求，提示升级 CLI。
5. **Verification**: 运行 server spec/unit 测试、CLI spec 测试，重点覆盖单仓库升级、多仓库 default 规范化、default 删除拒绝、project create --path 成功与失败路径。

## Open Questions

- 是否需要在 migration 中把无法修复的 legacy Project 标记为 archived/readonly，而不是完全阻塞启动？（当前提案要求停止升级，但可讨论降级体验。）
- `repo update` 失败后，CLI table 输出是否应该展示当前仓库状态以方便用户确认？
- 仓库资源名是否引入限制字符集（如 DNS-label 或仅 `[a-zA-Z0-9_-]`）？当前 spec 只要求非空，可考虑与 Project name 规则保持一致。
- Web UI 是否需要同步隐藏旧的 project-level path/branch 展示，避免用户困惑？本 issue 声明不加 Web 管理能力，但展示层可能需要小调整。
