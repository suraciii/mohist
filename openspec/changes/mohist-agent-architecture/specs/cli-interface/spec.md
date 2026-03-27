## ADDED Requirements

### Requirement: CLI 提供 attach 命令
CLI SHALL 提供 `mo attach <issue-id>` 命令，连接终端到 issue 的实时会话。

#### Scenario: Attach to issue
- **WHEN** 用户执行 `mo attach 42`
- **THEN** CLI 连接到 Mohist server 的 event bus
- **AND** 订阅 issue #42 的所有事件
- **AND** 将 agent 输出渲染到终端
- **AND** 从 stdin 读取用户输入并发布为事件

#### Scenario: Exit attach
- **WHEN** 用户按 Ctrl+C 或输入 `exit`
- **THEN** CLI 断开 event bus 连接
- **AND** agent 会话不受影响继续运行
