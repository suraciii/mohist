# OpenSpec Actions

## `mohist/openspec-tasks`

加载 `tasks.json`，把其中的任务加入当前 Workflow 执行。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `path` | 是 | — | `tasks.json` 的路径。类型为文本。 |
| `task` | 否 | — | 应用于每个任务条目的默认任务级字段。类型为对象；该值在任务展开时解析。 |
| `items` | 否 | `tasks` | JSON 文档中任务列表的顶层路径。类型为文本。 |
| `buildPrompt` | 否 | — | 构建任务提示词的文本。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `loaded` | 加入本次运行的任务数量。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `missing-source` | `tasks.json` 文件不存在。 |
| `server-unavailable` | 无法连接 Server。 |

### 示例

```yaml
- id: load-tasks
  uses: mohist/openspec-tasks
  with:
    path: openspec/changes/issue-448/tasks.json
    task: ${{ vars.defaultTask }}
    items: tasks
```

本示例引用名为 `defaultTask` 的 Variable；如不需要默认任务级字段，可省略 `task`。

## `mohist/openspec-artifacts`

检查指定 OpenSpec change 目录中的必需产物是否齐全。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `changeDir` | 是 | — | OpenSpec change 目录的路径。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `changeDir` | 解析后的 change 目录。 |
| `present` | 所有必需产物是否存在。 |
| `missing` | 缺失产物路径列表。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `artifacts-missing` | 必需的 OpenSpec 产物不存在。 |

### 示例

```yaml
- id: verify-change
  uses: mohist/openspec-artifacts
  with:
    changeDir: openspec/changes/issue-448
```

## `mohist/archive-change`

归档 OpenSpec change 目录，并提交这次移动产生的变更。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `changeDir` | 是 | — | OpenSpec change 目录的路径。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `source` | 源 change 目录。 |
| `destination` | 归档目标目录。 |
| `changed` | 归档步骤是否修改了仓库。 |
| `noChange` | 归档步骤是否没有产生变更。 |
| `commitMessage` | 归档步骤修改仓库时使用的提交消息。 |
| `commitSha` | 归档步骤修改仓库时产生的提交 SHA。 |
| `commitOutput` | 原始 Git 提交输出。 |
| `changedFiles` | 归档提交修改的文件。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `retry-safe` | 归档步骤可以安全重试。 |
| `partial-archive` | 源目录和归档目录都包含文件，因此拒绝覆盖。 |
| `missing-source` | 源 change 目录不存在。 |
| `config-error` | 归档配置无效。 |

### 示例

```yaml
- id: archive-change
  uses: mohist/archive-change
  with:
    changeDir: openspec/changes/issue-448
```
