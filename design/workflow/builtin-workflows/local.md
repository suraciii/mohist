---
purpose: "mohist/local：本地 squash 后直推 base branch。"
style: ["极简，只给目标态。"]
---

# mohist/local

目标：最短路径把已验证改动落到 base branch。

## Definition

```yaml
- stage: plan
  tasks:
    - proposal
    - specs
    - design
    - tasks
    - self-review
  checks:
    - name: plan-artifacts
      title: Plan artifacts complete
      uses: mohist/openspec-artifacts
      with:
        changeDir: ${{ openspecChangeDir }}
    - name: self-review-passed
      title: Self review passed
      uses: core/marker
      with:
        path: ${{ openspecChangeDir }}/self-review.md
        expect: <promise>PASS</promise>
    - name: health
      title: Health
      uses: core/script
      with:
        run: git diff --check
  requiresApproval: true

- stage: build
  tasks:
    - load-tasks
  checks:
    - health
    - verify

- stage: check
  tasks:
    - ai-review
  checks:
    - health
    - review-passed
    - merge-ready
  requiresApproval: true

- stage: integrate
  lockBehavior: sequential
  resources:
    - project-integration
  tasks:
    - id: integrate:archive-change
      uses: mohist/archive-change
    - id: integrate:rebase
      uses: mohist/rebase
      with:
        squash: true
        messageFrom: issue.title
    - id: integrate:push
      uses: mohist/push
```

## Rules

- `integrate:rebase` fetch 最新 base，解决冲突，并把工作分支 squash 成一个 commit。
- `messageFrom: issue.title` 决定 squash commit subject。
- `integrate:push` fast-forward 推送到 base branch。
- GitHub 上不产生 PR 记录。
