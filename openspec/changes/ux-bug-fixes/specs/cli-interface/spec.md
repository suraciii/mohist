## MODIFIED Requirements

### Requirement: CLI 阶段名与实现一致

CLI 输出中的阶段名 SHALL 使用当前实现的阶段名：`draft`、`plan`、`build`、`check`、`done`。

#### Scenario: issue list 显示正确阶段名
- **WHEN** 用户运行 `mo issue list`
- **THEN** 阶段列显示 `plan`/`build`/`check`/`done`（而非旧的 `designing`/`implementing`）

### Requirement: CLI 命令在 server 不可用时给出友好提示

所有需要 server 的 CLI 命令 SHALL 在执行前检查 server 是否可用。server 不可用时 SHALL 打印友好错误信息并退出，而非抛出 ECONNREFUSED。

#### Scenario: server 未启动时执行 issue list
- **WHEN** 用户运行 `mo issue list`
- **AND** mohist server 未运行
- **THEN** CLI 输出 "Server is not running. Start with: mo server start" 并以非零 exit code 退出
- **AND** 不输出 Node.js 的 ECONNREFUSED 堆栈信息
