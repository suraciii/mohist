---
purpose: "mohist/github-pr：GitHub draft PR -> ready PR -> squash merge。"
style: ["极简，只给目标态。"]
---

# mohist/github-pr

目标：通过 GitHub PR 交付；PR merge 成功才表示集成完成。

PR 生命周期：plan 文档产出后打开 draft PR；check 阶段完成 AI review、
标记 ready、PR 状态检查和人工批准；integrate 阶段推送 workflow branch，
然后等待 PR checks 并 squash merge。

不引入 `ready` stage，不引入 check-level `verifyTask`。自动修复通过 task
`recovery` 表达；恢复 task 完成后使用 `retrySelf: true` 重新运行原失败 task。

## Definition

```yaml
- stage: plan
  tasks:
    - id: proposal
      title: Generate proposal
      uses: mohist/acp-agent
      with:
        session: plan
        prompt: ${{ prompts.proposal }}
        agent: ${{ vars.agent }}
      expect:
        files:
          - path: ${{ openspecChangeDir }}/proposal.md
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/proposal.md

    - id: specs
      title: Write specs
      uses: mohist/acp-agent
      with:
        session: plan
        prompt: ${{ prompts.specs }}
        agent: ${{ vars.agent }}
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/specs

    - id: design
      title: Create design
      uses: mohist/acp-agent
      with:
        session: plan
        prompt: ${{ prompts.design }}
        agent: ${{ vars.agent }}
      expect:
        files:
          - path: ${{ openspecChangeDir }}/design.md
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/design.md

    - id: tasks
      title: Generate tasks
      uses: mohist/acp-agent
      with:
        session: plan
        prompt: ${{ prompts.tasks }}
        agent: ${{ vars.agent }}
      expect:
        files:
          - path: ${{ openspecChangeDir }}/tasks.json
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/tasks.json

    - id: self-review
      title: Self review
      uses: mohist/acp-agent
      with:
        session: plan
        prompt: ${{ prompts.self-review }}
        agent: ${{ vars.agent }}
        expect:
          markers:
            - path: ${{ openspecChangeDir }}/self-review.md
              oneOf:
                - <promise>PASS</promise>
                - <promise>FAIL</promise>
              failIf: FAIL
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/self-review.md
      recovery:
        budget: 2
        handlers:
          - when: promise=FAIL
            tasks:
              - id: recover:fix-plan-review
                title: Fix plan review findings
                uses: mohist/acp-agent
                with:
                  session: plan
                  prompt: ${{ prompts.fix-plan-review }}
                  agent: ${{ vars.agent }}
            retrySelf: true
    - id: open-draft-pr
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
  checks:
    - name: plan-artifacts
      title: Plan artifacts complete
      uses: mohist/openspec-artifacts
      with:
        changeDir: ${{ openspecChangeDir }}
      # proposal.md, design.md, tasks.json 是硬性要求。
      # specs/ 可选——纯性能/重构 issue 无 spec 变更时合法缺失。
  requiresApproval: true

- stage: build
  tasks:
    - id: load-tasks
      title: Load tasks from plan
      uses: mohist/openspec-tasks
  checks:
    - name: verify
      title: Build & full test suite
      uses: core/script
      with:
        run: ${{ vars.ci.verify }}
      repairLimit: 2
      repairTask:
        id: fix-tests
        title: Fix failing tests/build
        uses: mohist/acp-agent
        with:
          session: build
          prompt: ${{ prompts.fix-tests }}
          agent: ${{ vars.agent }}

- stage: check
  tasks:
    - id: ai-review
      title: AI review
      uses: mohist/acp-agent
      with:
        session: check
        prompt: ${{ prompts.review }}
        agent: ${{ vars.agent }}
        expect:
          markers:
            - path: ${{ openspecChangeDir }}/review.md
              oneOf:
                - <promise>PASS</promise>
                - <promise>FAIL</promise>
              failIf: FAIL
      artifacts:
        files:
          - path: ${{ openspecChangeDir }}/review.md
      recovery:
        budget: 2
        handlers:
          - when: promise=FAIL
            tasks:
              - id: recover:fix-review-findings
                title: Fix review findings
                uses: mohist/acp-agent
                with:
                  session: check
                  prompt: ${{ prompts.auto-fix }}
                  agent: ${{ vars.agent }}
            retrySelf: true

    - id: push
      title: Push
      uses: mohist/push
      with:
        source: ${{ workspace.branch }}
        target: ${{ workspace.branch }}
        remote: origin
        forceWithLease: true

    - id: mark-pr-ready
      title: Mark GitHub PR ready
      uses: mohist/mark-github-pr-ready
      with:
        prNumber: ${{ vars.github.pr.number }}

  checks:
    - name: github-pr-status
      title: GitHub PR status
      uses: mohist/github-pr-status
      with:
        prNumber: ${{ vars.github.pr.number }}

  requiresApproval: true

- stage: integrate
  lockBehavior: sequential
  resources:
    - project-integration
  tasks:
    - id: spec-sync
      title: Sync specs
      uses: mohist/acp-agent
      with:
        session: integrate
        prompt: ${{ prompts.spec-sync }}
        agent: ${{ vars.agent }}
      # specs/ 不存在时（纯性能/重构 issue），spec-sync 跳过，不算失败。

    - id: archive-change
      title: Archive change
      uses: mohist/archive-change
      with:
        changeDir: ${{ openspecChangeDir }}

    - id: push
      title: Push
      uses: mohist/push
      with:
        source: ${{ workspace.branch }}
        target: ${{ workspace.branch }}
        remote: origin
        forceWithLease: true

    - id: merge-pr
      title: Merge GitHub PR
      uses: mohist/merge-github-pr
      with:
        prNumber: ${{ vars.github.pr.number }}
        method: squash
      recovery:
        budget: 2
        handlers:
          - when: errorCode=base-moved
            tasks:
              - id: recover:rebase
                title: Rebase after base moved
                uses: mohist/rebase
                with:
                  baseBranch: ${{ repository.baseBranch }}
                  remote: origin
                  squash: false
                  recovery:
                    budget: 1
                    handlers:
                      - when: failureKind=conflict
                        tasks:
                          - id: recover:resolve-rebase-conflicts
                            title: Resolve rebase conflicts
                            uses: mohist/acp-agent
                            with:
                              session: integrate
                              prompt: ${{ prompts.resolve-rebase-conflicts }}
                              agent: ${{ vars.agent }}
              - id: recover:push
                title: Push
                uses: mohist/push
                with:
                  source: ${{ workspace.branch }}
                  target: ${{ workspace.branch }}
                  remote: origin
                  forceWithLease: true
            retrySelf: true

          - when: errorCode=pr-checks-failed
            tasks:
              - id: recover:fix-pr-checks
                title: Fix failing GitHub PR checks
                uses: mohist/acp-agent
                with:
                  session: integrate
                  prompt: ${{ prompts.fix-pr-checks }}
                  agent: ${{ vars.agent }}
              - id: recover:push
                title: Push
                uses: mohist/push
                with:
                  source: ${{ workspace.branch }}
                  target: ${{ workspace.branch }}
                  remote: origin
                  forceWithLease: true
            retrySelf: true

  checks:
    - name: merge-verified
      title: Merge verified
      uses: mohist/github-pr-status
      with:
        prNumber: ${{ vars.github.pr.number }}
        expect: merged
```

## Rules

- `open-draft-pr` 是 plan stage 最后一个 task。它创建或复用同
  head/base 的 draft PR，并写入 `vars.github.pr.number` /
  `vars.github.pr.url`。
- `check` stage 在 `ai-review` 通过后执行 `push` 同步最新代码到远程 PR，
  再执行 `mark-pr-ready` 标记 PR ready，然后运行只读的 `github-pr-status`
  check 确认 PR 状态，等待人工批准。
- `github-pr-status` 有两种使用模式：check 阶段做只读确认（PR 已 ready）；
  integrate 阶段用 `expect: merged` 验证 PR 已合入。
- `ai-review` 的 `expect.markers.oneOf: [PASS, FAIL]` 保证 agent 产出合规
  marker；`failIf: FAIL` 让 action 在 marker 匹配到 FAIL 时返回失败，output
  中 `promise` 字段值为 `FAIL`。recovery handler 用 `when: promise=FAIL`
  匹配。auto-fix 是 recovery handler 的 task；修复后 `retrySelf: true`
  重新运行 `ai-review`。`self-review` 同理。
- `mark-pr-ready` 只需要 `prNumber`，且必须幂等：PR 已经 ready 时
  成功返回。
- `push` 是显式同步 task。它把本地线性 workflow branch
  推到同名远程 branch；rebase 后允许 `forceWithLease` 更新 PR head。
- `push` 不声明业务 recovery。若 push 失败，说明权限、
  网络或远程 branch 被外部写入，应作为普通 task failure 暴露。
- `merge-pr` 等待 GitHub PR checks，通过后 squash merge。
- `base-moved` 属于 `merge-pr` 的 recovery（`when: errorCode=base-moved`）：rebase、push、
  然后 `retrySelf: true` 重新运行原 merge task。不重新 mark ready。
- rebase 如果发生冲突，`mohist/rebase` 必须返回 `output.failureKind: conflict`，
  并保留 rebase 进行中。随后 rebase 的 recovery handler（`when: failureKind=conflict`）
  触发显式 `recover:resolve-rebase-conflicts` task。
  该 task 负责解决冲突并完成正在进行的 rebase；随后 workflow 继续执行
  `recover:push`，再重试 merge。
- recovery task 的 prompt 使用命名引用（`${{ prompts.resolve-rebase-conflicts }}`、
  `${{ prompts.fix-pr-checks }}`）。prompt 模板内部可以引用 `${{ failure.output }}`
  获取当前失败 task 的上下文；不在 workflow YAML 中使用内联 prompt 字符串。
  - `resolve-rebase-conflicts` 必须指导 agent：解决冲突、完成 rebase、并确保
    最终 commit 已推送到远程（`${{ workspace.branch }}` 和
    `${{ repository.baseBranch }}` 在 prompt 模板展开上下文中可用）。
  - `fix-pr-checks` 必须指导 agent：修复失败的 GitHub PR checks、并确保修复后
    的 commit 已推送到远程 PR branch。
- `pr-checks-failed` 属于 `merge-pr` 的 recovery（`when: errorCode=pr-checks-failed`）：agent 修复失败
  checks、push，然后 `retrySelf: true` 重新运行原 merge task。
- PR checks 是 `merge-github-pr` 的内部前置条件，不是 stage check。
- PR 相关副作用必须显式出现在 task graph；不使用 stage hook 或隐藏边界动作。
