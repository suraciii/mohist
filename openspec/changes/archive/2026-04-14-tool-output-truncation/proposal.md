## Why

Explore agent 的工具（glob、read_file）没有输出大小限制，导致工具结果可能远超模型上下文窗口。例如 `glob("**/*")` 在包含大型参考源码（opensrc/）的项目中可返回数千行路径，直接撑爆 MiniMax-M2.7 的上下文窗口，请求以 400 错误中断。当前只有 grep 工具有 200 条结果上限。

## What Changes

- glob 工具增加结果数量上限（100 条），超出时截断并提示
- read_file 工具增加行数上限（2000 行）、单行长度上限（2000 字符）、总字节上限（50KB），超出时截断并提示用 offset/limit 继续读取
- 新增全局工具输出截断服务（Truncate），作为所有工具的安全网，统一兜底超限输出
- 工具注册机制增加自动包装器，每个工具的 execute 结果自动经过 Truncate 处理

## Capabilities

### New Capabilities

- `tool-output-limits`: glob 和 read_file 工具的内置输出大小限制
- `truncate-service`: 全局工具输出截断服务，自动截断超限的工具输出并写入磁盘

### Modified Capabilities

（无现有 spec 需要修改）

## Impact

- **工具代码**: `src/tools/glob-tool.ts`、`src/tools/read-file.ts` 增加内部限制
- **工具框架**: `src/agent-runtime/tool.ts` 增加全局截断包装器
- **新模块**: `src/services/truncate-service.ts` 截断服务
- **新目录**: `~/.mohist/tool-output/` 存放截断后的完整输出
- **无 breaking change**: 所有改动向后兼容，只是截断过大的输出
