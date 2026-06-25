---
purpose: "mohist/github-pr：GitHub draft PR -> ready PR -> squash merge。"
style: ["极简，只给目标态。"]
---

# mohist/github-pr

目标：通过 GitHub PR 交付；PR merge 成功才表示集成完成。

PR 生命周期：plan 文档产出后打开 draft PR，check 最后标记 ready，integrate 最后 merge。
`verifyTasks` 是目标 schema：repair 后按顺序追加这些验证 task。

## Definition

```yaml
- stage: plan
  tasks:
    - proposal
    - specs
    - design
    - id: plan:open-draft-pr
      title: Open draft GitHub PR
      uses: mohist/create-github-pr
      with:
        source: ${{ workspace.branch }}
        target: ${{ repository.baseBranch }}
        remote: origin
        draft: true
        titleFrom: issue.title
        bodyFrom: issue.body
      setVars:
        github.pr.number: output.prNumber
        github.pr.url: output.prUrl
    - tasks
    - self-review
  checks:
    - proposal-complete
    - specs-complete
    - design-complete
    - tasks-valid
    - name: self-review-passed
      repairTask: fix-plan-review
      verifyTasks:
        - plan:open-draft-pr
    - name: health
      repairTask: fix-plan-health
  requiresApproval: true

- stage: build
  tasks:
    - id: load-tasks
      title: Load tasks from plan
      uses: mohist/openspec-tasks
  checks:
    - name: health
      repairTask: fix-build-health
    - name: verify
      repairTask: fix-tests

- stage: check
  tasks:
    - id: ai-review
      title: AI review
      uses: mohist/acp-agent

    - id: check:ready-pr
      title: Mark GitHub PR ready
      uses: mohist/ready-github-pr
      with:
        prNumber: ${{ vars.github.pr.number }}
        source: ${{ workspace.branch }}
        target: ${{ repository.baseBranch }}
        remote: origin
        titleFrom: issue.title
        bodyFrom: issue.body
      setVars:
        github.pr.number: output.prNumber
        github.pr.url: output.prUrl

  checks:
    - name: health
      repairTask: fix-check-health
      verifyTasks:
        - check:ready-pr

    - name: review-passed
      repairTask: fix-review-findings
      verifyTasks:
        - ai-review
        - check:ready-pr

    - name: merge-ready
      repairTask: rebase-onto-base
      verifyTasks:
        - check:ready-pr
  requiresApproval: true

- stage: integrate
  lockBehavior: sequential
  resources:
    - project-integration
  tasks:
    - id: integrate:spec-sync
      title: Sync specs
      uses: mohist/acp-agent
    - id: integrate:archive-change
      title: Archive change
      uses: mohist/archive-change
    - id: integrate:merge-pr
      title: Merge GitHub PR
      uses: mohist/merge-github-pr
      with:
        prNumber: ${{ vars.github.pr.number }}
        method: squash
        subjectFrom: issue.title
      onFailure:
        limit: 1
        cases:
          - when:
              output.errorCode: base-moved
            tasks:
              - id: recover:rebase
                title: Rebase after base moved
                uses: mohist/rebase
                with:
                  baseBranch: ${{ repository.baseBranch }}
                  remote: origin
                  squash: false
                  conflictResolver:
                    title: Resolve rebase conflicts
                    with:
                      description: Resolve rebase conflicts, stage resolved files, and continue until the rebase completes.
              - id: recover:merge-pr
                title: Merge GitHub PR
                uses: mohist/merge-github-pr
                with:
                  prNumber: ${{ vars.github.pr.number }}
                  method: squash
                  subjectFrom: issue.title
  checks:
    - name: health
      repairTask: fix-integrate-health
```

## Rules

- `plan:open-draft-pr` 位于 `design` 后、`tasks` / `self-review` 前：plan
  docs 已能说明意图，因此这是最早可开 draft PR 的节点。
- `plan:open-draft-pr` 创建或复用同 head/base 的 draft PR，并写入
  `vars.github.pr.number` / `vars.github.pr.url`。
- plan repair 如果修改已推送内容，verify path 重新运行 `plan:open-draft-pr`
  以刷新 draft PR。
- `check:ready-pr` 是 check 阶段最后一个静态 task：推送最新
  `workspace.branch`，更新 PR title/body，并 mark ready。
- `review-passed` repair 必须重新跑 `ai-review`，不能只修改代码后检查旧
  `review.md`。
- `integrate:merge-pr` 等待 PR checks，通过后 squash merge。
- merge 失败恢复只处理预期中的 base moved / out-of-date 路径：rebase 后重试
  merge；不重新 ready PR，不处理配置、认证、PR 状态冲突或 GitHub API 异常。
- PR checks 是 `merge-github-pr` 的内部前置条件，不是 stage check。
- PR 相关副作用必须显式出现在 task graph；不使用 stage hook 或隐藏边界动作。
