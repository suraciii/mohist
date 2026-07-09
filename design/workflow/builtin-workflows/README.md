# Built-in Workflows

Mohist 内置 workflow：

- [`mohist/local`](local.md) — 本地 squash 后直推 base branch。
- [`mohist/github-pr`](github-pr.md) — 通过 GitHub draft PR -> ready PR -> squash merge 交付。

`mohist/local` 是默认 workflow。选择 `mohist/github-pr`：

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

或在 issue frontmatter 里声明：

```yaml
---
recommended_workflow: mohist/github-pr
recommended_workflow_reason: This issue should be integrated through a traceable GitHub PR.
risk: medium
---
```
