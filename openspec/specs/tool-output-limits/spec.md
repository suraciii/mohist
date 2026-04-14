## ADDED Requirements

### Requirement: Glob 工具结果数量上限
glob 工具 SHALL 最多返回 100 条匹配结果。超出时 SHALL 截断为前 100 条，并追加提示信息建议使用更精确的 pattern 或指定路径。

#### Scenario: 匹配结果在限制内
- **WHEN** glob pattern 匹配到 50 个文件
- **THEN** 返回全部 50 个文件路径，无截断提示

#### Scenario: 匹配结果超过限制
- **WHEN** glob pattern 匹配到 300 个文件
- **THEN** 返回前 100 个文件路径，追加提示 `(Results truncated: showing first 100 results. Use a more specific path or pattern.)`

### Requirement: Read 工具行数上限
read_file 工具 SHALL 最多返回 2000 行内容。超出时 SHALL 截断并追加提示，建议使用 offset/limit 参数分段读取。

#### Scenario: 文件行数在限制内
- **WHEN** 读取一个 500 行的文件且未指定 limit
- **THEN** 返回全部 500 行内容

#### Scenario: 文件行数超过限制
- **WHEN** 读取一个 5000 行的文件且未指定 limit
- **THEN** 返回前 2000 行，追加提示 `(Showing lines 1-2000 of 5000. Use offset=2001 to continue.)`

#### Scenario: 指定 offset 和 limit 时不受上限影响
- **WHEN** 读取一个 5000 行的文件且指定 offset=1, limit=2000
- **THEN** 返回指定范围的 2000 行

### Requirement: Read 工具总字节上限
read_file 工具 SHALL 最多返回 50KB (51200 bytes) 内容。即使行数未超过 2000 行，若总字节数超过 50KB 也 SHALL 截断。

#### Scenario: 总字节数超过限制
- **WHEN** 读取的文件前 2000 行总大小为 80KB
- **THEN** 返回不超过 50KB 的内容，追加提示说明总字节数超限及如何继续读取

### Requirement: Read 工具单行长度上限
read_file 工具返回的每一行 SHALL 最多包含 2000 个字符。超出的行 SHALL 截断并追加 `... (line truncated to 2000 chars)` 后缀。

#### Scenario: 某行超过单行长度上限
- **WHEN** 文件第 10 行有 5000 个字符
- **THEN** 该行截断为前 2000 个字符 + `... (line truncated to 2000 chars)`
