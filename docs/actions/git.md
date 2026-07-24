# Git Actions

Git Action 的仓库、分支和远程仓库都通过显式 `with` 输入确定；Action 不会从 Variables 读取隐式回退值，Git 命令始终使用主机提供的工作区。

以下示例中的 `${{ repository.baseBranch }}` 和 `${{ workspace.branch }}` 来自当前运行的仓库与工作区，`origin` 是显式指定的远程仓库名。表达式的完整规则见 [Workflow Definition 参考](../workflow-definition.md#模板表达式)。

## `mohist/workspace-prepare`

将工作区重置到预期分支，并清理残留的本地 Git 状态。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `expectedBranch` | 是 | — | 预期的工作区分支。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 状态标识。 |
| `expectedBranch` | 预期的分支名。 |
| `head` | 准备完成后的 HEAD 快照。 |
| `residual` | 准备完成后的残留状态快照。 |
| `porcelain` | 准备完成后的 Porcelain 状态。 |
| `step` | 失败时产生快照的步骤。 |
| `workDir` | 工作区目录。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `workspace-setup` | 工作区准备失败。 |

### 示例

```yaml
- id: prepare-workspace
  uses: mohist/workspace-prepare
  with:
    expectedBranch: ${{ workspace.branch }}
```

## `mohist/rebase`

将当前分支变基到基础分支，并可选地把变基后的提交压缩为一个提交。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `baseBranch` | 是 | — | 基础分支名。类型为文本。 |
| `remote` | 否 | — | Git 远程仓库名。类型为文本。 |
| `squash` | 否 | `false` | 是否把变基后的提交压缩为一个提交。类型为布尔。 |
| `message` | 否 | — | 直接指定的压缩提交消息。类型为文本。 |
| `messageFrom` | 否 | — | 压缩提交消息的 Issue 字段来源。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 变基状态标识。 |
| `baseBranch` | 基础分支名。 |
| `remote` | Git 远程仓库名。 |
| `baseRef` | 解析后的基础引用。 |
| `rebasedOntoSha` | 变基开始时基础引用的顶端提交 SHA。 |
| `beforeHeadSha` | 变基前的 HEAD SHA。 |
| `afterHeadSha` | 变基后的 HEAD SHA。 |
| `squashed` | 是否执行了压缩步骤。 |
| `squashedHeadSha` | 压缩后的 HEAD SHA。 |
| `rebased` | 变基是否成功。 |
| `conflicts` | 尚未解决冲突的文件。 |
| `rebaseLeftInProgress` | 是否留下了进行中的变基。 |
| `output` | 聚合后的 Git 输出。 |
| `steps` | 每个步骤的 Git 命令结果。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `abort-failed` | 中止已有变基失败。 |
| `fetch-failed` | 获取基础分支失败。 |
| `base-resolve-failed` | 解析基础引用失败。 |
| `prepare-failed` | 变基前准备工作区失败。 |
| `rebase-failed` | 变基因未指明的原因失败。 |
| `conflict` | 变基遇到冲突。 |
| `squash-failed` | 压缩步骤失败。 |

### 示例

```yaml
- id: rebase-onto-base
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
    squash: false
```

## `mohist/rebase-status`

报告工作区当前的变基状态。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `baseBranch` | 是 | — | 基础分支名。类型为文本。 |
| `remote` | 否 | — | Git 远程仓库名。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 状态标识（已验证或失败）。 |
| `baseBranch` | 基础分支名。 |
| `remote` | Git 远程仓库名。 |
| `baseRef` | 解析后的基础引用。 |
| `rebaseInProgress` | 是否有正在进行的变基。 |
| `conflicts` | 尚未解决冲突的文件。 |
| `baseSha` | 基础引用的顶端提交 SHA。 |
| `headSha` | 当前 HEAD SHA。 |
| `mergeBaseSha` | HEAD 与基础引用的合并基点 SHA。 |
| `output` | 聚合后的 Git 输出。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `rebase-incomplete` | 变基未完成或工作区不干净。 |

### 示例

```yaml
- id: check-rebase
  uses: mohist/rebase-status
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

## `mohist/merge-ready`

报告当前工作区是否可以合并到基础分支。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `baseBranch` | 是 | — | 基础分支名。类型为文本。 |
| `remote` | 是 | — | Git 远程仓库名。类型为文本。 |
| `source` | 是 | — | 源分支名。类型为文本。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `targetBranch` | 基础分支名。 |
| `strategy` | 合并策略标识。 |
| `baseSha` | 基础引用的顶端提交 SHA。 |
| `candidateHeadSha` | 源引用的顶端提交 SHA。 |
| `mergeBaseSha` | 源分支与基础分支的合并基点 SHA。 |
| `canMerge` | 是否可以合并。 |
| `conflictFiles` | 尚未解决冲突的文件。 |
| `checkedAt` | 检查时间的 ISO 时间戳。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `merge-not-ready` | 当前状态尚未满足合并条件。 |

### 示例

```yaml
- id: verify-merge-ready
  uses: mohist/merge-ready
  with:
    baseBranch: ${{ repository.baseBranch }}
    source: ${{ workspace.branch }}
    remote: origin
```

## `mohist/push`

将工作区的源分支推送到目标分支。

### 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `source` | 是 | — | 源分支。类型为文本。 |
| `target` | 是 | — | 目标分支。类型为文本。 |
| `remote` | 是 | — | Git 远程仓库名。类型为文本。 |
| `force` | 否 | `false` | 是否使用 `--force` 推送。类型为布尔。 |
| `forceWithLease` | 否 | `false` | 是否使用 `--force-with-lease` 推送。类型为布尔。 |

### 输出

| 字段 | 含义 |
|---|---|
| `kind` | 输出类型标识。 |
| `status` | 推送状态标识。 |
| `source` | 源分支。 |
| `target` | 目标分支。 |
| `remote` | Git 远程仓库名。 |
| `refspec` | 解析后的引用规格。 |
| `workDir` | 工作区目录。 |
| `landedCommit` | 被推送的顶端提交。 |
| `pushed` | 推送是否成功。 |
| `force` | 是否使用了强制模式。 |
| `forceWithLease` | 是否使用了带租约的强制模式。 |
| `output` | 聚合后的 Git 推送输出。 |
| `steps` | 每个步骤的 Git 命令结果。 |

### 业务错误码

| 错误码 | 含义 |
|---|---|
| `base-moved` | 目标分支已移动，推送不是快进更新。 |
| `push-failed` | 推送因未指明的原因失败。 |

### 示例

```yaml
- id: push-branch
  uses: mohist/push
  with:
    source: ${{ workspace.branch }}
    target: ${{ repository.baseBranch }}
    remote: origin
    force: false
    forceWithLease: false
```
