## Why

`mo` 的现有命令树、帮助和 Mohist Skill 仍同时暴露旧动词、重复入口和实现细节，使人和 Agent 无法只依赖当前版本的命令面可靠地发现并执行操作。前置领域切片已交付，现需收口为一套稳定、可发现且不重复的操作语言，避免后续能力继续扩大迁移面。

## What Changes

- 将资源读取与修改收敛为 `view`、`edit` 等规范动词，并保留领域状态动作在唯一的所属命令组中。
- **BREAKING** 移除资源动词的并列旧路径、重复的命令入口，以及没有明确资源所有者的旧 area；不提供内建迁移 alias。
- 建立根、命令组和叶子帮助的分层内容与错误反馈契约，使帮助保持本地、无副作用并能引导到下一层或确定的恢复动作。
- 将打包的 Mohist Skill 收敛为渐进披露的决策入口：负责场景判断和关键状态选择，精确语法交由当前 `mo --help` 发现。
- 更新用户可见 CLI 参考与示例，使文档、Skill、帮助和可执行命令树表达同一规范路径。

## Capabilities
- `cli-command-language`: 定义 `mo` 的规范命令树、唯一动词和资源归属，并移除重复或遗留入口。
- `cli-help`: 定义根、命令组、叶子帮助及用法错误的分层发现和可执行反馈行为。
- `mohist-skill`: 定义打包 Mohist Skill 的渐进披露边界、场景路由和对运行时帮助的交接。

## Impact

- `packages/cli/Mohist.Cli/` 的 System.CommandLine 命令树、帮助文本、错误呈现和打包 skill-data。
- `packages/cli/tests/Mohist.Cli.Tests/` 的命令解析、帮助、错误和 Skill 示例契约测试。
- `docs/cli-reference.md`、`design/cli.md` 及引用旧 `mo` 路径的用户指南。
- 人工操作、Agent 自动化和已使用被移除命令的外部脚本需切换到新的规范路径。
