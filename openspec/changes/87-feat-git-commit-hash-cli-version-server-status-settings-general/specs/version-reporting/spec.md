## ADDED Requirements

### Requirement: 统一版本获取模块

系统 SHALL 提供 `getVersionInfo()` 函数，返回 `{ version: string, gitHash: string | null, versionString: string }`。`version` 从 package.json 的 `version` 字段读取。`gitHash` 通过 `git rev-parse --short HEAD` 在运行时获取（相对于 mohist 安装目录）。当 git 命令失败时，`gitHash` SHALL 为 `null`。`versionString` SHALL 格式化为 `"{version} ({gitHash})"` 当 gitHash 可用时，否则为 `"{version}"`。

#### Scenario: git 可用时的版本获取
- **WHEN** `getVersionInfo()` 被调用
- **AND** mohist 安装目录下 `git rev-parse --short HEAD` 成功返回
- **THEN** 返回 `{ version: "x.y.z", gitHash: "abc1234", versionString: "x.y.z (abc1234)" }`

#### Scenario: git 不可用时的 fallback
- **WHEN** `getVersionInfo()` 被调用
- **AND** `git rev-parse --short HEAD` 失败（非 git 目录、git 未安装等）
- **THEN** 返回 `{ version: "x.y.z", gitHash: null, versionString: "x.y.z" }`

### Requirement: Server 启动日志记录版本

Server 启动时 SHALL 将版本信息作为第一条结构化日志输出，格式为 `Mohist v{versionString} starting...`。

#### Scenario: Server 启动时输出版本日志
- **WHEN** mohist server 启动
- **THEN** 第一条日志输出包含 `Mohist v0.1.0 (abc1234) starting...` 格式
- **AND** 该日志在所有其他初始化日志之前
