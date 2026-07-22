# GitHub PR Actions

GitHub PR Action 的仓库、分支和 Pull Request 身份都通过显式 `with` 输入确定；Action 不会从 Variables 读取隐式回退值，并始终使用主机提供的工作区。

以下示例中的 `${{ repository.gitUrl }}`、`${{ repository.baseBranch }}` 和 `${{ workspace.branch }}` 来自当前运行的仓库与工作区；`${{ vars.github.pr.number }}` 是前序 `mohist/create-github-pr` 输出 `prNumber` 写入的 Run Variable。表达式与 Variables 的完整规则见 [Workflow Definition 参考](../workflow-definition.md#模板表达式)。

## `mohist/create-github-pr`

为当前分支新建或更新 GitHub Pull Request。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `repositoryUrl` | 是 | — | 用于确定 GitHub 仓库的 Git 仓库 URL。类型为文本。 |
| `source` | 是 | — | 源分支。类型为文本。 |
| `target` | 是 | — | 目标分支。类型为文本。 |
| `draft` | 否 | `true` | 是否以草稿状态打开 Pull Request。类型为布尔。 |
| `title` | 否 | — | 直接指定的 Pull Request 标题。类型为文本。 |
| `message` | 否 | — | `title` 的别名。类型为文本。 |
| `titleFrom` | 否 | `issue.title` | Pull Request 标题的 Issue 字段来源。类型为文本。 |
| `body` | 否 | — | 直接指定的 Pull Request 正文。类型为文本。 |
| `bodyFrom` | 否 | `issue.body` | Pull Request 正文的 Issue 字段来源。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | Pull Request 状态标识。 |
| `source` | 源分支。 |
| `targetBranch` | 目标分支。 |
| `branch` | Head 分支名。 |
| `prNumber` | Pull Request 编号。 |
| `prUrl` | Pull Request URL。 |
| `operation` | 操作标识（`created`、`updated` 或 `reused`）。 |
| `draft` | Pull Request 是否为草稿。 |
| `output` | 聚合后的 `gh` 输出。 |
| `steps` | 每个步骤的 `gh` 命令结果。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `config-error` | GitHub 配置缺失或无效。 |
| `protection-conflict` | 分支保护规则拒绝了 Pull Request。 |
| `base-moved` | 基础分支已移动，Pull Request 已过期。 |
| `pr-state-conflict` | 已有 Pull Request 处于冲突状态。 |
| `retry-safe` | Pull Request 操作可以安全重试。 |
| `create-pr-failed` | 创建 Pull Request 失败。 |

### 示例

```yaml
- id: open-draft-pr
  uses: mohist/create-github-pr
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    source: ${{ workspace.branch }}
    target: ${{ repository.baseBranch }}
    draft: true
    titleFrom: issue.title
    bodyFrom: issue.body
```

## `mohist/mark-github-pr-ready`

将指定的 GitHub Pull Request 标记为可供审查；已经就绪时保持幂等。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `repositoryUrl` | 是 | — | 用于确定 GitHub 仓库的 Git 仓库 URL。类型为文本。 |
| `prNumber` | 是 | — | Pull Request 编号。类型为数值。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 状态标识。 |
| `prNumber` | Pull Request 编号。 |
| `prUrl` | Pull Request URL。 |
| `state` | 操作后的 Pull Request 状态。 |
| `previousState` | 操作前的 Pull Request 状态。 |
| `transitioned` | 是否发生了就绪状态转换。 |
| `output` | 聚合后的 `gh` 输出。 |
| `steps` | 每个步骤的 `gh` 命令结果。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `config-error` | GitHub 配置缺失或无效。 |
| `protection-conflict` | 分支保护规则拒绝了状态转换。 |
| `base-moved` | 基础分支已移动，Pull Request 已过期。 |
| `pr-state-conflict` | 已有 Pull Request 处于冲突状态。 |
| `retry-safe` | 本次操作可以安全重试。 |
| `mark-ready-failed` | 将 Pull Request 标记为就绪失败。 |

### 示例

```yaml
- id: mark-pr-ready
  uses: mohist/mark-github-pr-ready
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

## `mohist/merge-github-pr`

使用压缩合并方式合并指定的 GitHub Pull Request。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `repositoryUrl` | 是 | — | 用于确定 GitHub 仓库的 Git 仓库 URL。类型为文本。 |
| `method` | 否 | `squash` | 合并方式；仅支持 `squash`。类型为文本。 |
| `prNumber` | 是 | — | Pull Request 编号。类型为数值。 |
| `subject` | 否 | — | 直接指定的压缩提交标题。类型为文本。 |
| `subjectFrom` | 否 | `issue.title` | 压缩提交标题的 Issue 字段来源。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 合并状态标识。 |
| `prNumber` | Pull Request 编号。 |
| `prUrl` | Pull Request URL。 |
| `mergeCommitSha` | 压缩合并提交的 SHA。 |
| `method` | 实际使用的合并方式。 |
| `output` | 聚合后的 `gh` 输出。 |
| `steps` | 每个步骤的 `gh` 命令结果。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `base-moved` | 基础分支已移动，Pull Request 已过期。 |
| `retry-safe` | 合并操作可以安全重试。 |
| `config-error` | GitHub 配置缺失或无效。 |
| `protection-conflict` | 分支保护规则拒绝了合并。 |
| `pr-state-conflict` | 已有 Pull Request 处于冲突状态。 |
| `pr-checks-unavailable` | 无法取得 Pull Request checks 状态。 |
| `pr-checks-failed` | 必需的 Pull Request checks 未通过。 |
| `merge-failed` | 合并 Pull Request 失败。 |

### 示例

```yaml
- id: merge-pr
  uses: mohist/merge-github-pr
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    subjectFrom: issue.title
```

## `mohist/github-pr-status`

验证指定的 GitHub Pull Request 是否处于预期状态。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `repositoryUrl` | 是 | — | 用于确定 GitHub 仓库的 Git 仓库 URL。类型为文本。 |
| `prNumber` | 是 | — | Pull Request 编号。类型为数值。 |
| `expect` | 否 | `open,ready` | 逗号分隔的预期状态，可使用 `open`、`ready` 或 `merged`。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 状态标识。 |
| `prNumber` | Pull Request 编号。 |
| `prUrl` | Pull Request URL。 |
| `prState` | Pull Request 状态。 |
| `isDraft` | Pull Request 是否为草稿。 |
| `expectations` | 预期状态标记。 |
| `missing` | 未满足的预期状态标记。 |
| `output` | 聚合后的 `gh` 输出。 |
| `steps` | 每个步骤的 `gh` 命令结果。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `pr-status-failed` | Pull Request 状态检查失败。 |

### 示例

```yaml
- id: verify-pr-status
  uses: mohist/github-pr-status
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
    expect: open,ready
```

## `mohist/github-pr-checks`

等待指定 GitHub Pull Request 的全部 checks 通过。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `repositoryUrl` | 是 | — | 用于确定 GitHub 仓库的 Git 仓库 URL。类型为文本。 |
| `prNumber` | 是 | — | Pull Request 编号。类型为数值。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | Check 状态标识。 |
| `prNumber` | Pull Request 编号。 |
| `pollIntervalMs` | 轮询间隔，单位为毫秒。 |
| `message` | 面向使用者的检查结果。 |
| `output` | 聚合后的 `gh` 输出。 |
| `steps` | 每个步骤的 `gh` 命令结果。 |

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `config-error` | GitHub 配置缺失或无效。 |
| `pr-checks-unavailable` | 无法取得 Pull Request checks 状态。 |
| `pr-checks-failed` | 必需的 Pull Request checks 未通过。 |
| `aborted` | 轮询已取消。 |

### 示例

```yaml
- id: wait-for-pr-checks
  uses: mohist/github-pr-checks
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```
