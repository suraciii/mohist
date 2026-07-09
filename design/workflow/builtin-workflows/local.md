# mohist/local

Shortest path: squash and push to base branch.

```
plan → approval → build → check → approval → integrate (sequential, project-integration lock)
```

build: `load-tasks`.
check: `ai-review` with `when: promise=FAIL` recovery. 
integrate: `archive-change` → `rebase --squash` (message from `issue.title`) → `push`.

No PR. No GitHub.
