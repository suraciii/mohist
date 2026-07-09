# mohist/github-pr

Deliver via GitHub draft PR → ready PR → squash merge.

```
plan → approval → build → check → approval → integrate (sequential, project-integration lock)
```

## Plan

`proposal` → `specs` → `design` → `tasks` → `self-review` (recovery: `when: promise=FAIL` → fix + retrySelf) → `open-draft-pr` (creates draft PR, sets `vars.github.pr.{number,url}`).

Checks: `plan-artifacts` (proposal + specs + design + tasks), `self-review-passed`, `health`.

## Build

`load-tasks` → `verify` (recovery: `when: errorCode=script-failed` → fix + retrySelf).

## Check

`ai-review` (recovery: `when: promise=FAIL` → fix + retrySelf) → `push` (forceWithLease sync to PR head) → `mark-pr-ready` (idempotent).

Check: `github-pr-status` (read-only PR status confirmation).

## Integrate

`archive-change` → `push` → `merge-pr` (waits PR checks, squash merges).

merge-pr recovery:
- `when: errorCode=base-moved` → rebase → push → retrySelf
  - rebase `when: errorCode=conflict` → resolve-conflicts agent
- `when: errorCode=pr-checks-failed` → fix agent → push → retrySelf

Check: `github-pr-status` with `expect: merged`.

## Rules

- PR checks = merge action internal precondition, not stage check.
- All PR side effects are explicit tasks. No hidden stage boundary hooks.
- `push` declares no recovery. Push failure = ordinary task failure.
- Recovery prompts use named references (`${{ prompts.resolve-rebase-conflicts }}`). Prompt templates can access `${{ failure.output }}`.
- resolve-rebase-conflicts agent must: resolve conflicts, complete rebase, push.
- fix-pr-checks agent must: fix failing checks, push to PR branch.
