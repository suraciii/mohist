# Built-in Workflows

The authoritative content is `*.workflow.yaml` under
`packages/server/src/Mohist.Server/Workflow/Services/Profiles/`. A `mohist/*`
Profile appears in every Project's WorkflowProfile collection, but the current
Mohist version owns its Definition. It is not copied into editable Project
data. An upgrade updates these Profiles. An active Run reads the updated
Definition when it initializes a later Stage. An initialized Stage or dispatched
task is not rewritten retroactively.

A built-in Profile cannot be modified or deleted. Create a Project Profile for
custom behavior. This document records rationale and invariants without
duplicating YAML.

- `mohist/local`: Rebase with squash locally, then push directly to the base
  branch. This is the default.
- `mohist/github-pr`: Deliver through a draft Pull Request, ready transition,
  and squash merge.

Select one with:

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

## Shared Structure

Both Workflows use the same main path:

```text
plan -> approval -> build -> check -> approval -> integrate
                                                   sequential, with project-integration lock
```

- Every Stage begins with `workspace-prepare`, so a task always executes in a
  prepared Workspace.
- **plan:** `proposal -> specs -> design -> tasks -> self-review`.
  `self-review` declares `<promise>PASS/FAIL</promise>` through
  `expect.markers`. `failIf` maps FAIL to task failure. A recovery handler with
  `when: output.promise=FAIL` creates a repair task and then `retrySelf`. The
  `plan-artifacts` Stage Check uses `mohist/openspec-artifacts` to verify all
  OpenSpec artifacts.
- **build:** `load-tasks` uses `mohist/openspec-tasks` to expand `tasks.json`.
  `mohist/openspec-task-prompt` composes each Prompt. `verify` runs
  `vars.ci.verify`. On failure, default recovery `recover:fix-ci` diagnoses and
  repairs before `retrySelf`.
- **check:** `ai-review` uses the same promise-marker and recovery pattern as
  `self-review`.
- **Approval feedback:** Top-level `approval.feedback.task` declares
  `apply-feedback`. It uses the rejected Stage's Session name so feedback repair
  continues in that Stage context.
- **Rebase conflicts use task-level recovery:** `mohist/rebase` returns
  `error.code: conflict` and leaves rebase in progress. A nested handler assigns
  an Agent to resolve conflicts and complete rebase. It does not use
  `retrySelf` because the Agent completes the operation. The recovery Prompt is
  a named reference such as `${{ prompts.resolve-rebase-conflicts }}` and can
  access `${{ failure.error }}`.

See [`recovery.md`](recovery.md) for recovery and
[`actions.md`](actions.md) for Action contracts.

## `mohist/local`

This is the shortest delivery path and has no GitHub dependency or Pull
Request.

- **check** adds a `merge-ready` task that is absent from `github-pr`. Before
  Approval, it confirms that the branch can merge into the base. When
  `canMerge=false`, it rebases onto the base with nested conflict recovery and
  then uses `retrySelf`. At Approval, the branch is mergeable, so Integrate does
  not fail only because it is behind.
- **integrate:** `archive-change`, with retryable error codes mapped directly to
  `retrySelf`, then `rebase --squash` with `issue.title` as commit message, then
  `push` to the base branch.
- Every Stage has a `git diff --check` health task; Plan uses a Stage Check. On
  `error.code=script-failed`, an Agent repairs only whitespace or patch-format
  problems before `retrySelf`.

## `mohist/github-pr`

This Profile opens a draft Pull Request after Plan, marks it ready after Check
Approval, and squash-merges during Integrate. Runner host must have an
authenticated `gh` CLI for the target Repository.

Workspace is a rebuildable execution copy. The remote Workflow branch is the
recovery point between Stages. The Pull Request is a review projection of that
branch. Before passing output to another Stage, Approval, or Pull Request
operation, every Repository-modifying Stage explicitly pushes current HEAD to
the Workflow branch. The Profile orders these tasks. Runner only executes and
reports facts. There is no implicit Stage hook.

### Pull Request Identity and Metadata

- After self-review, Plan runs `push` and then `open-draft-pr`. The latter
  creates or reuses one draft Pull Request. `setVars` writes `output.prNumber`
  and `output.prUrl` to `vars.github.pr.{number,url}`. Pull Request identity is
  a Workflow Runtime Variable. Later Stages reference it and do not open another
  Pull Request.
- Pull Request title and body do not come from Workflow metadata.
  `titleFrom: issue.title` and `bodyFrom: issue.body` direct
  `mohist/create-github-pr` to read Issue data at runtime.

### Check and Integrate

- **build:** After `verify` passes, `push` publishes the verified result so a
  later Stage can rebuild its Workspace on another Runner.
- **check:** After `ai-review`, run `push`, idempotent `mark-pr-ready`, and
  `verify-pr-checks`. `mark-pr-ready` reads only `vars.github.pr.number`; an
  already-ready Pull Request succeeds without changing title, body, or code.
  `mohist/github-pr-checks` polls checks. On `pr-checks-failed`, it creates
  `recover:fix-pr-checks`, then `recover:push`, then `retrySelf`, symmetric with
  merge recovery. The `github-pr-status` Stage Check reads and confirms state.
- **integrate:** Run `archive-change`, `push`, and `merge-pr`.
  `mohist/merge-github-pr` waits for checks, squash-merges, and rereads until
  `state=MERGED`. Stage Check `merge-verified` uses `github-pr-status` with
  `expect: merged` for read-only confirmation.

Approval feedback is ordered work: the Agent applies feedback, pushes current
HEAD, and then reruns Stage Checks. The Pull Request and recoverable branch
contain feedback output before the next Approval.

### `merge-pr` Recovery

`mohist/merge-github-pr` reports recoverable failure through Action-owned
`error.code`. The Profile declares all handling under `merge-pr.recovery`; there
is no Stage hook or implicit boundary action.

- `error.code=base-moved`: Run `recover:rebase` with `squash: false` and nested
  Agent conflict resolution, then force `recover:push`, then `retrySelf`.
- `error.code=pr-checks-failed`: Run `recover:fix-pr-checks`, then
  `recover:push` with force-with-lease, then `retrySelf`.
- `error.code=protection-conflict`: Run `retrySelf` directly.

### Invariants

- Pull Request checks appear at two explicit boundaries: the Check Stage
  `verify-pr-checks` task for repair before delivery, and the internal
  prerequisite of Integrate `merge-pr` immediately before merge. Both use the
  same polling and classification functions and `pr-checks-failed` code, with
  symmetric recovery.
- Pull Request checks are an internal merge-Action prerequisite, not a Stage
  Check.
- Every publish and Pull Request side effect is an explicit task. No implicit
  Stage-boundary hook exists.
- `push` has no business recovery. Failure indicates permission, network, or an
  externally modified remote branch and surfaces as ordinary task failure.
- `recover:resolve-rebase-conflicts` resolves conflicts and completes rebase.
  `recover:fix-pr-checks` only repairs checks. A later explicit `recover:push`
  always owns push.

Every Agent task uses `mohist/opencode` with
`options: ${{ vars.agent }}`. `expect` is a task-level completion contract at
the same level as `with`, `artifacts`, `setVars`, and `recovery`.
`apply-feedback` binds `options` explicitly and respects Issue-level model
selection. See [`actions.md`](actions.md) for the complete contract.
