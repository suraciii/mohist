# Workflow Definition 参考

Workflow Profile 的 definition 是一份 YAML 文档，声明 Issue 走完 Workflow 的阶段、初始
任务、检查、审批点和产生后续任务的规则。本篇是编写 definition 的完整语法参考。Profile
的选择与管理见 [Workflow Profile](workflow-profiles.md)。

运行期间，retry、recovery、审批反馈和 `mo issue rebase` 等控制命令还可以产生新的任务。
这些任务属于当前 WorkflowRun，不会改写 definition。

## 顶层结构

definition 顶层只有两个部分：

```yaml
approval:      # 可选。审批驳回后的反馈修复任务
  feedback:
    tasks:       # 有序任务列表
      - <Task>

stages:        # 必填。有序阶段列表
  - <Stage>
```

审批驳回时，Mohist 按顺序执行 `approval.feedback.tasks` 来应用反馈。第一个任务通常延续
被驳回阶段的会话；之后的任务可以把修复后的成果发布出去。全部完成后，阶段检查重新执行，
审批者看到的是已发布的当前成果。

## Stage

```yaml
- stage: integrate          # 必填。阶段名
  requiresApproval: true    # 可选，默认 false。阶段完成后等待审批
  lockBehavior: sequential  # 可选。该阶段串行执行，必须同时声明 resources
  resources:
    - project-integration   # 锁名。同名锁的阶段同一时间只有一个在执行
  tasks:                    # 必填。有序任务列表
    - <Task>
  checks:                   # 可选。阶段完成前的验证
    - <Check>
```

审批者是谁、决策怎样给出，见[核心概念的 Approval 节](concepts.md#approval审批)。

## Task

```yaml
- id: merge-pr                   # 必填。阶段内的任务标识
  title: Merge GitHub PR         # 可选。面向使用者的名称
  uses: mohist/merge-github-pr   # 必填。选择 Action
  with:                          # 可选。Action 输入，支持模板表达式
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
  expect: <Expect>               # 可选。本任务的完成要求
  artifacts:                     # 可选。需要采集的产物
    files:
      - path: <path>
  setVars:                       # 可选。把任务输出写入本次 Run 的 Variables
    github.pr.number: output.prNumber
  recovery: <Recovery>           # 可选。失败恢复声明
```

可用的 Action 与各自的输入输出见 [Action 契约](actions/README.md)。每个 Action 声明
自己的输入(名称、是否必填、默认值)、输出字段与错误码;`with` 按声明校验——未知
字段、缺少必填字段、类型不符都会被拒绝,而不是被静默忽略。

### expect —— 完成要求

```yaml
expect:
  files:                    # 这些文件必须存在
    - path: <path>
  markers:                  # 内容必须命中 oneOf 之一，否则任务失败
    - path: <path>          # 或特殊值 _output：检查 Agent 本回合的最终答复文本
      oneOf:
        - <promise>PASS</promise>
        - <promise>FAIL</promise>
      failIf: <promise>FAIL</promise>   # 可选。命中该文本时任务失败
```

`expect` 是 Workflow 对任务的完成要求，不属于 Action 输入。Action 执行失败会让任务
失败；Action 成功后，`expect` 不满足也会让任务失败。

必须产出的文件同时写进 `expect.files` 和 `artifacts.files`；可选产物只写 `artifacts`。

### artifacts —— 产物采集

采集是尽力而为：文件不存在时跳过，不让任务失败。采集到的产物永久保存，可在任务详情
中查看。

### setVars —— 输出写入 Variables

左侧是 `vars` 下的路径，右侧是任务输出中的字段路径。任务成功后写入本次 Run 的
Variables，供后续任务用 `${{ vars.* }}` 读取。任一字段写入失败时任务失败，Variables
保持不变。恢复任务可以覆盖相同的值。

### recovery —— 失败恢复

```yaml
recovery:
  budget: 2                 # 可选，默认 0。一轮连续自动恢复的上限
  handlers:                 # 有序，命中第一个匹配的 handler
    - when: error.code=conflict  # 可选。按结果上下文 path=值 匹配
      tasks:                # 可选。恢复任务，可以嵌套自己的 recovery
        - <Task>
      retrySelf: true       # 可选，默认 false。恢复任务之后重试原任务
```

- handler 至少声明 `tasks` 或 `retrySelf` 之一。
- 声明 `when` 的 handler 按顺序匹配结果上下文，与任务成败无关：成功任务的 output 命中
  `when: output.promise=FAIL` 同样触发恢复；失败任务使用 `when: error.code=...`。
- 可省略 `when` 声明一个默认 handler。每个 recovery 最多一个默认 handler，且必须排在
  最后；它只在任务失败且前面的显式 handler 均未命中时触发。
- 恢复任务是真实的 Workflow 任务，出现在进度和时间线中。
- 预算用尽后不再自动恢复，任务失败并暴露原因；人工 retry 开启新一轮，重新获得完整
  预算。

## Check

```yaml
- id: merge-verified        # 必填。阶段内的检查标识
  title: Merge verified     # 可选。面向使用者的名称
  uses: mohist/github-pr-status
  with:
    repositoryUrl: ${{ repository.gitUrl }}
    prNumber: ${{ vars.github.pr.number }}
```

阶段的全部 check 通过后阶段才完成；check 失败时 Workflow 不进入下一阶段。

## 模板表达式

`with` 和 `expect` 中可以使用 `${{ }}` 表达式。下表列出全部可用的命名空间，表外的
根引用不合法：

| 表达式 | 含义 |
|---|---|
| `workflow.runId` | 本次运行的标识 |
| `stage.name` | 当前阶段名 |
| `work.id` | 本次任务执行的工作标识 |
| `issue.number` | Issue 编号 |
| `repository.*` | 目标仓库信息，如 `repository.baseBranch` |
| `workspace.*` | 工作区信息，如 `workspace.branch` |
| `vars.*` | 合并后的 Variables（[合并规则](workflow-profiles.md#vars-references)） |
| `tasks.<id>.outputs.*` | 先前任务的输出 |
| `prompts.<key>` | Project Prompt，任务执行时读取正文 |
| `failure.output` | 仅恢复任务可用：触发恢复的那次任务 output |
| `failure.error.code` | 仅恢复任务可用：触发恢复的 error code |
| `failure.error.message` | 仅恢复任务可用：触发恢复的 error message |

- 模板在任务开始执行前展开；已开始任务的输入固定，不随之后的 Variables 修改变化。
- `${{ prompts.<key> }}` 例外：执行时才读取 Prompt 正文。
- `${{ vars.x }}` 单独占据整个值时，替换结果保留原始类型（对象、数组、数字）。
- 表达式可以嵌在字符串里拼接，如 `openspec/changes/issue-${{ issue.number }}`：值转为
  文本拼入。嵌入的表达式解析不出值、或值是对象/数组时，任务失败。
- 需要字面 `${{` 时写 `\${{`。

## 完整示例

经 GitHub PR 交付的最小 profile，每个构造出现一次：

```yaml
approval:
  feedback:
    task:
      id: apply-feedback
      uses: mohist/opencode
      with:
        session: ${{ stage.name }}
        prompt: ${{ prompts.apply-feedback }}
        options: ${{ vars.agent }}

stages:
  - stage: plan
    requiresApproval: true
    tasks:
      - id: proposal
        uses: mohist/opencode
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
        expect:
          files:
            - path: docs/proposal.md
          markers:
            - path: _output
              oneOf:
                - <promise>done</promise>
                - <promise>unfinished</promise>
              failIf: <promise>unfinished</promise>
        artifacts:
          files:
            - path: docs/proposal.md
      - id: publish-plan
        uses: mohist/push
        with:
          source: HEAD
          target: ${{ workspace.branch }}
          remote: origin
          force: true
      - id: open-draft-pr
        uses: mohist/create-github-pr
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          source: ${{ workspace.branch }}
          target: ${{ repository.baseBranch }}
          draft: true
          titleFrom: issue.title
          bodyFrom: issue.body
        setVars:
          github.pr.number: output.prNumber
          github.pr.url: output.prUrl
    checks:
      - id: health
        uses: core/script
        with:
          run: git diff --check
          timeout: 300000

  - stage: integrate
    lockBehavior: sequential
    resources:
      - project-integration
    tasks:
      - id: merge-pr
        uses: mohist/merge-github-pr
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          method: squash
        recovery:
          budget: 2
          handlers:
            - when: error.code=base-moved
              tasks:
                - id: recover:rebase
                  uses: mohist/rebase
                  with:
                    baseBranch: ${{ repository.baseBranch }}
                    remote: origin
                  recovery:
                    budget: 2
                    handlers:
                      - when: error.code=conflict
                        tasks:
                          - id: recover:resolve-conflicts
                            uses: mohist/opencode
                            with:
                              session: integrate
                              prompt: ${{ prompts.resolve-rebase-conflicts }}
                              options: ${{ vars.agent }}
                - id: recover:push
                  uses: mohist/push
                  with:
                    source: ${{ workspace.branch }}
                    target: ${{ workspace.branch }}
                    remote: origin
                    force: true
              retrySelf: true
            - when: error.code=protection-conflict
              retrySelf: true
    checks:
      - id: merge-verified
        uses: mohist/github-pr-status
        with:
          repositoryUrl: ${{ repository.gitUrl }}
          prNumber: ${{ vars.github.pr.number }}
          expect: merged
```

## 实装差距

- 部分内置 profile 仍把 `expect` 写在 `with` 内；目标位置是任务顶层。
- 部分内置任务仍使用旧的 Agent Action 输入；目标接口以 [Action 契约](actions/README.md)
  为准。
- 内置 profile 仍直接使用 `${{ openspecChangeDir }}`；目标写法是字面模板
  `openspec/changes/issue-${{ issue.number }}`。
- 字符串内嵌入的表达式解析不出值时，当前保留原文而非让任务失败。
- check 当前用 `name` 声明标识；目标是 `id`。
- 本地校验（写完 definition 不经服务器即可验证并得到领域语言的错误提示）尚未提供。
- `with` 尚未按 Action 声明校验：当前未知输入字段被静默忽略，缺失必填字段到任务运行
  时才报错；目标是保存 Profile 时即拒绝。
