## MODIFIED Requirements

### Requirement: CLI 支持 server 命令

CLI SHALL 支持 server 管理命令（无需 server 运行），包括 install、uninstall、restart、update 子命令。

#### Scenario: 启动 server
- **WHEN** 用户执行 `mo server start`
- **THEN** CLI 启动 server 进程
- **AND** CLI 等待 server 就绪
- **AND** CLI 显示 "Server started"

#### Scenario: 停止 server
- **WHEN** 用户执行 `mo server stop`
- **THEN** CLI 发送停止信号给 server
- **AND** CLI 显示 "Server stopped"

#### Scenario: 安装 systemd 服务
- **WHEN** 用户执行 `mo server install`
- **THEN** CLI 安装 mohist 为 systemd 用户服务
- **AND** CLI 显示安装结果

#### Scenario: 卸载 systemd 服务
- **WHEN** 用户执行 `mo server uninstall`
- **THEN** CLI 卸载 mohist systemd 用户服务
- **AND** CLI 显示卸载结果

#### Scenario: 重启 server (systemd 已安装)
- **WHEN** 用户执行 `mo server restart`
- **AND** systemd 服务已安装
- **THEN** CLI 执行 `systemctl --user restart mohist.service`
- **AND** CLI 显示 "Server restarted (systemd)"

#### Scenario: 重启 server (无 systemd)
- **WHEN** 用户执行 `mo server restart`
- **AND** systemd 服务未安装
- **THEN** CLI 执行 stop 然后执行 start（复用现有 spawn 逻辑）
- **AND** CLI 显示 "Server restarted"

#### Scenario: 更新 server (源码模式)
- **WHEN** 用户执行 `mo server update`
- **THEN** CLI 执行源码 rebuild + systemd restart
- **AND** CLI 显示每步结果

#### Scenario: mo server --help 显示新命令
- **WHEN** 用户执行 `mo server --help`
- **THEN** 输出包含 `install`、`uninstall`、`restart`、`update` 子命令
