## ADDED Requirements

### Requirement: MergeQueue 根据文件重叠度串行化 issue merge

MergeQueue SHALL 在选择下一个待处理 issue 时，检查 pending 队列中各 issue 修改的文件列表，如果多个 issue 修改了重叠的文件，SHALL 按入队顺序串行处理这些 issue，避免并发 rebase 导致同一文件的冲突。

#### Scenario: 无文件重叠时按入队顺序处理

- **WHEN** MergeQueue 有多个 pending 状态的 issue
- **AND** 这些 issue 修改的文件集合互不重叠
- **THEN** 系统按入队顺序（FIFO）逐个处理

#### Scenario: 文件重叠时串行处理

- **WHEN** MergeQueue 有多个 pending 状态的 issue
- **AND** issue A 和 issue B 修改了至少 1 个相同文件
- **THEN** 系统按入队顺序先处理 issue A
- **AND** issue A 合并完成后，issue B 的 rebase 将基于包含 issue A 更改的最新 master
- **AND** 这降低了 issue B rebase 冲突的概率

#### Scenario: 获取 issue 修改的文件列表

- **WHEN** MergeQueue 需要检测文件重叠
- **THEN** 系统对每个 pending issue 执行 `git diff --name-only <baseBranch>...<issueBranch>` 获取修改文件列表
- **AND** 缓存结果避免重复计算（同一 issue 在 rebase 前只计算一次）

#### Scenario: 重叠检测失败时降级为 FIFO

- **WHEN** MergeQueue 尝试获取文件列表失败（如分支不存在）
- **THEN** 系统降级为普通 FIFO 顺序处理
- **AND** 记录警告日志
