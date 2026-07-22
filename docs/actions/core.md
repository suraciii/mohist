# Core Actions

## `core/process`

运行一个进程并采集标准输出和退出码。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `command` | 是 | — | 要调用的命令。类型为文本。 |
| `args` | 否 | `[]` | 传给命令的参数。类型为数组。 |

### 输出

| 字段 | 含义 |
|---|---|
| `stdout` | 去除首尾空白后的命令标准输出。 |
| `exitCode` | 进程退出码。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `process-failed` | 进程以非零状态退出。 |

### 示例

```yaml
- id: check-version
  uses: core/process
  with:
    command: node
    args: [--version]
```

## `core/script`

通过当前平台的 Shell 包装器运行一段内联脚本。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `run` | 是 | — | 要运行的脚本内容。类型为文本。 |
| `shell` | 否 | — | Shell 可执行文件。类型为文本。 |
| `timeout` | 否 | — | 脚本执行期限，单位为毫秒。类型为数值。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `run` | 原样返回的脚本内容。 |
| `shell` | 实际使用的 Shell 可执行文件。 |
| `exitCode` | Shell 退出码。 |
| `stdout` | 截断后的标准输出。 |
| `stderr` | 截断后的标准错误输出。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `script-failed` | 脚本以非零状态退出。 |

### 示例

```yaml
- id: verify-diff
  uses: core/script
  with:
    run: git diff --check
```

## `core/artifact-exists`

检查工作区内一个相对路径的文件或目录是否存在。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `path` | 是 | — | 要检查的路径。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `path` | 解析后的路径。 |
| `exists` | 路径是否存在。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `artifact-missing` | 必需的文件或目录不存在。 |

### 示例

```yaml
- id: check-proposal
  uses: core/artifact-exists
  with:
    path: openspec/changes/issue-448/proposal.md
```

## `core/marker`

检查工作区内文件是否包含指定的标记文本。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `path` | 是 | — | 要读取的路径。类型为文本。 |
| `expect` | 否 | — | 要匹配的标记文本。类型为文本。 |
| `contains` | 否 | — | `expect` 的旧版别名；类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `path` | 解析后的路径。 |
| `marker` | 本次检查所匹配的标记文本。 |
| `found` | 是否找到标记。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `artifact-missing` | 标记文件不存在。 |
| `marker-missing` | 文件中未找到标记文本。 |

### 示例

```yaml
- id: verify-completion
  uses: core/marker
  with:
    path: openspec/changes/issue-448/progress.txt
    expect: "## Codebase Patterns"
```
