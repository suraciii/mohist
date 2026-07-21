# GitHub PR Actions

GitHub PR Actions take repository and pull request identity from explicit
`with` fields. They use the host-provided workspace and do not read repository,
branch, or PR fallbacks from Variables.

## `mohist/create-github-pr`

Required inputs: `repositoryUrl` (`string`), `source` (`string`), and `target` (`string`).

Optional inputs: `draft` (`boolean`, default `true`), `title`/`message`, `titleFrom` (default `issue.title`), `body`, and `bodyFrom` (default `issue.body`).

## `mohist/mark-github-pr-ready`

Required inputs: `repositoryUrl` (`string`) and `prNumber` (`number`). The action is idempotent when the PR is already ready.

## `mohist/merge-github-pr`

Required inputs: `repositoryUrl` (`string`) and `prNumber` (`number`). Optional inputs are `method` (`string`, default `squash`), `subject`, and `subjectFrom` (default `issue.title`). The action waits for checks and squash-merges the explicitly selected PR.

## `mohist/github-pr-status`

Required inputs: `repositoryUrl` (`string`) and `prNumber` (`number`). Optional input: `expect` (`string`, default `open,ready`).

Variables must be bound explicitly through `with`:

```yaml
with:
  repositoryUrl: ${{ repository.gitUrl }}
  prNumber: ${{ vars.github.pr.number }}
```
