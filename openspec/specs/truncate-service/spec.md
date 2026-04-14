## ADDED Requirements

### Requirement: 全局工具输出截断
TruncateService SHALL 作为全局安全网，自动检查所有工具的输出大小。当输出超过 2000 行或 50KB 时 SHALL 截断并写入磁盘文件。

#### Scenario: 工具输出在限制内
- **WHEN** 工具返回 500 行、20KB 的文本
- **THEN** 原样返回，不截断

#### Scenario: 工具输出超过行数限制
- **WHEN** 工具返回 3000 行的文本
- **THEN** 保留前 2000 行作为预览，完整内容写入 `~/.mohist/tool-output/` 下的文件，追加截断提示和文件路径

#### Scenario: 工具输出超过字节限制
- **WHEN** 工具返回 500 行但总大小为 80KB 的文本
- **THEN** 保留不超过 50KB 的前 N 行作为预览，完整内容写入磁盘，追加截断提示和文件路径

### Requirement: 截断提示信息
截断后的输出 SHALL 包含有用的提示信息，指导 agent 如何获取完整内容。

#### Scenario: 截断提示内容
- **WHEN** 工具输出被截断
- **THEN** 提示信息包含：截断了多少行/字节、完整输出文件路径、建议使用 read_file 的 offset/limit 参数读取或使用 grep 搜索

### Requirement: 截断文件存储
截断的完整输出 SHALL 写入 `~/.mohist/tool-output/` 目录。

#### Scenario: 文件写入成功
- **WHEN** 工具输出需要截断
- **THEN** 完整输出写入 `~/.mohist/tool-output/tool_<timestamp>_<random>.txt`，返回的 content 中包含该文件路径

#### Scenario: 目录不存在时自动创建
- **WHEN** `~/.mohist/tool-output/` 目录不存在
- **THEN** 自动创建目录后再写入文件

### Requirement: 工具执行自动包装
Tool.define() 的 execute SHALL 自动经过 TruncateService 处理。工具开发者无需手动调用截断逻辑。

#### Scenario: 新工具自动获得截断保护
- **WHEN** 一个新注册的工具返回超限输出
- **THEN** 输出自动被截断，无需工具自身做任何处理
