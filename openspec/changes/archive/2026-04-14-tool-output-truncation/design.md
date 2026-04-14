## Context

mohist 的 explore agent 使用 `streamText()` + 多步工具调用（`stepCountIs(20)`），每步的工具调用和结果会累积到下一次 API 请求中。当前 glob 和 read_file 工具没有输出大小限制，导致单步工具结果可能远超模型上下文窗口，触发 MiniMax 的 `context window exceeds limit (2013)` 错误。

opencode（参考项目）已实现三层防御：
- Layer 1: 工具内部限制（glob 100条、read 2000行/50KB、grep 100条）
- Layer 2: 全局 Truncate 服务（tool.ts 包装器，自动截断超限输出并写入磁盘）
- Layer 3: Context window 管理（pruning + compaction）

本变更实现 Layer 1 和 Layer 2，Layer 3 留给后续。

## Goals / Non-Goals

**Goals:**
- glob 工具输出限制在 100 条结果以内
- read_file 工具输出限制在 2000 行 / 50KB / 每行 2000 字符以内
- 全局截断服务作为安全网，自动截断任何工具的超限输出
- 截断时提供有用提示，引导 agent 用更精确的方式获取数据

**Non-Goals:**
- Context window 的 pruning 和 compaction（Layer 3）
- 动态 token budget（需要知道模型 context window 大小）
- 截断文件的自动清理（可后续迭代）

## Decisions

### Decision 1: glob 结果上限 100 条

opencode 用 100 条上限。mohist 的项目结构类似（包含参考源码目录），100 条足够让 agent 了解项目结构，同时不会撑爆上下文。

超出时按原始顺序保留前 100 条，追加提示信息建议用更精确的 pattern 或指定路径。同时设置 `metadata.truncated = true`，告知全局截断包装器已处理。

**替代方案**: 按文件修改时间排序取最新的 100 条（opencode 的做法）。但这需要额外的 stat 调用，mohist 的 glob 实现是同步的简单遍历，保持简单优先。

### Decision 2: read_file 三重限制 2000行 / 50KB / 2000字符每行

与 opencode 保持一致的阈值。read_file 工具内部自行实现三重限制，生成定制化的截断提示（如 `Use offset=2001 to continue`），并设置 `metadata.truncated = true` 跳过全局二次截断。

这三个限制独立生效：
- 行数超 2000：截断，提示用 offset/limit 继续
- 总字节超 50KB：截断，提示用 offset/limit 继续
- 单行超 2000 字符：截断该行，加后缀提示

### Decision 3: 全局截断服务设计

参考 opencode 的 `Truncate` 服务，但简化实现（mohist 不使用 Effect 框架）：

```typescript
// src/services/truncate-service.ts

export interface TruncateResult {
  content: string;
  truncated: boolean;
  outputPath?: string;
}

export interface TruncateOptions {
  maxLines?: number;      // default 2000
  maxBytes?: number;      // default 51200 (50KB)
  direction?: 'head' | 'tail';  // default 'head'
}

export async function truncate(
  text: string,
  options?: TruncateOptions
): Promise<TruncateResult>
```

行为：
1. 文本在限制内 → 原样返回 `{ content: text, truncated: false }`
2. 超限 → 按 `direction` 取 head 或 tail 的预览，完整内容写入 `~/.mohist/tool-output/tool_<timestamp>_<random>.txt`，返回 `{ content: preview + hint, truncated: true, outputPath }`

### Decision 4: Tool.define 集成方式（关键机制）

需要扩展 `ToolDefinition` 的返回类型，支持 `metadata` 字段。包装器通过 `metadata.truncated` 判断工具是否已经自行处理了截断：

```typescript
export interface ToolResult {
  output: string;
  metadata?: {
    truncated?: boolean;
    outputPath?: string;
    [key: string]: unknown;
  };
}

export interface ToolDefinition<P = unknown> {
  id: string;
  description: string;
  parameters: z.ZodType<P>;
  execute: (params: P) => Promise<string | ToolResult>;
}
```

改造后的执行流程：

```
params → safeParse → execute(params) → rawResult
                                            │
                                            ▼
                                    ┌───────────────┐
                                    │  结果是 string? │
                                    │   包装成对象    │
                                    └───────┬───────┘
                                            │
                                            ▼
                                    ┌───────────────┐
                                    │ metadata.     │
                                    │ truncated     │
                                    │ === true ?    │
                                    └───────┬───────┘
                                            │
                              ┌─────────────┴─────────────┐
                              ▼                           ▼
                         YES (已处理)                 NO (未处理)
                              │                           │
                              ▼                           ▼
                    直接返回 output              调用 TruncateService
                                                       │
                                                       ▼
                                              返回截断后的 output
```

这样：
- **glob / read_file** 自己截断并设 `truncated = true` → 全局包装器跳过
- **grep** 已有 200 条限制，但未设置 `truncated` → 如果极端情况下 200 条仍超 50KB，全局包装器兜底
- **未来新工具** 不做任何限制 → 自动获得全局截断保护

### Decision 5: 截断文件存储位置

`~/.mohist/tool-output/` — 与 `~/.mohist/mohist.db` 同级，用户容易找到。文件名用 `tool_<timestamp>_<random>.txt` 避免冲突。

## Risks / Trade-offs

- **[截断可能丢失关键信息]** → 截断时写入磁盘，agent 可通过 read_file 分段读取完整输出
- **[固定阈值不适应所有模型]** → 当前 2000行/50KB 是保守值，对大多数模型安全；动态 token budget 可后续迭代
- **[磁盘文件无自动清理]** → 初期可接受，后续加定时清理（参照 opencode 的 7 天保留策略）
- **[工具级限制和全局限制的重复]** → 这是有意的两层防御。工具内部限制给用户更好的上下文提示，全局限制是兜底安全网
