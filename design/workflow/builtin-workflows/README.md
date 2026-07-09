# Built-in Workflows

- [`mohist/local`](local.md) — local squash → push. Default.
- [`mohist/github-pr`](github-pr.md) — draft PR → ready → squash merge.

Select workflow:
```bash
mo issue create "..." --workflow-profile mohist/github-pr
```
