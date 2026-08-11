# Built-in Workflows

The current executable definitions are
[`mohist-local.workflow.yaml`](../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml)
and
[`mohist-github-pr.workflow.yaml`](../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml).
A `mohist/*` Profile appears in every Project, but the current Mohist version
owns it. An upgrade changes future Stage initialization; it does not rewrite an
initialized Stage or dispatched Task.

A built-in Profile cannot be modified or deleted. Create a Project Profile for
custom behavior. This document is the target design contract; the Status section
identifies where the current YAML has not converged. After convergence, YAML is
the authority for exact Task IDs, Action inputs, and ordering.

- `mohist/local`: Rebase with squash locally, then push directly to the base
  branch. This is the default.
- `mohist/github-pr`: Separate planning, coding, and review among named Project
  Agents, then deliver through a draft Pull Request and GitHub Auto-merge.

Select one with:

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

## Design Drivers

- Local delivery and Pull Request delivery have different shared-boundary risks.
  Keeping two Profiles makes review, authentication, and recovery requirements
  explicit instead of hiding them behind conditional hooks.
- YAML is the executable behavioral authority after it converges on this design.
  This document explains why the Profiles differ and does not define exact Task
  IDs.
- Every side effect is an explicit Task. Explicit work can be retried, audited,
  and recovered with the same Action contract; an implicit Stage hook cannot.
- A Workspace is rebuildable. The remote branch is the Repository recovery
  boundary, and the Artifact Store is the Workspace-level file recovery
  boundary. Publishing and artifact capture must both be visible in the
  Definition.

## Shared Stage Model

Both Workflows retain the same stage names:

```text diagram
plan -> approval -> build -> check -> integrate
                              |
                              | local: Approval, then locked local integration
                              ` github-pr: reviewer gate, then GitHub Auto-merge
```

- Every Repository operation selects `repository.path` explicitly, so no Task
  treats the Workspace root as a Git checkout or reads `workspace.branch`.
- Plan validates direction before Build. Build changes the Repository. Check is
  a separate review pass so implementation is not its own acceptance signal.
- Approval feedback is ordered work in the rejected Stage's Session. This keeps
  feedback and repair context together and makes the next approval inspect the
  repaired result.
- Recovery belongs beside the Task whose failure it understands. A rebase
  conflict, CI failure, or review finding is handled explicitly; no hidden
  recovery hook runs at a Stage boundary.

See [`recovery.md`](recovery.md) for recovery and
[`actions.md`](actions.md) for Action contracts.

## `mohist/local`

This is the shortest delivery path and has no GitHub dependency or Pull Request.
Because no remote review gate proves mergeability, Check verifies that the Issue
branch can merge before asking for Approval. Integrate then archives the change,
rebases and squashes onto the current base, and pushes. Keeping the mergeability
check before Approval prevents a stale branch from turning an approved result
into a predictable Integrate failure.

Repository health checks remain explicit Tasks. Their recovery is limited to
the formatting or patch problem they detect and cannot become a general repair
hook.

## `mohist/github-pr`

This Profile requires active Project Agents whose static names are `planner`,
`coder`, and `reviewer`. Each `mohist/agent` task resolves the current definition
for its role at attempt dispatch. Missing roles fail with `agent_not_found`; the
Profile has no Inline Agent fallback and Variables cannot replace a role.

```text diagram
Plan:      planner -> DESIGN + PLAN -> Approval
Build:     coder -> verify -> publish branch -> draft PR
Check:     reviewer -> PASS? -> ready PR -> required checks
                       |
                       `-- FAIL -> coder repair -> verify -> publish -> review again
Integrate: GitHub Auto-merge -> confirm MERGED
```

The role working directories are intentionally different. Planner and reviewer
start at `workspace.path` because their durable output belongs under `PLANS/`.
Coder, scripts, and every Git or GitHub Action start at `repository.path`.
Selecting a starting directory does not create another Workspace or prevent an
Agent from reading the other reserved directories.

### Workspace Artifacts

Plan requires and captures exactly these files:

- `PLANS/issue-${{ issue.number }}-DESIGN.md`
- `PLANS/issue-${{ issue.number }}-PLAN.md`

Check requires and captures
`PLANS/issue-${{ issue.number }}-REVIEW.md`. The review contains one machine
decision, `PASS` or `FAIL`, plus findings. `FAIL` is recoverable output rather
than successful review. A coder repair never edits the decision to claim review
success; the reviewer produces the next decision.

Declared paths are Workspace-root relative even when the producing Action runs
in `repository.path`. The Artifact Store retains each successful capture. On a
new materialization, Workspace preparation restores the latest capture for a
path before dependent work starts. Git restores `REPOS/`; artifact restore is
for Workspace-level paths and must never write under `REPOS/`.

The Plan files are required captures, not merely required local files. Capture,
upload, and recording must succeed before Plan can wait for Approval. The same
rule applies to the review report before its verdict schedules repair or lets
Check pass. Restore considers successfully recorded captures from failed Task
attempts so a `FAIL` review remains available to its coder recovery.

This Profile does not invoke an OpenSpec Action and does not create or read an
`openspec/changes/` directory. OpenSpec Actions remain supported extension
points for custom Profiles.

### Prompt Contract

The Profile uses dedicated built-in Prompt keys `github-pr-plan`,
`github-pr-plan-feedback`, `github-pr-build`, `github-pr-review`,
`github-pr-repair`, and `github-pr-resolve-conflicts`. These Prompts are part of
the bundled Profile contract; the local OpenSpec Prompt set is not reused.

- Plan and plan-feedback Prompts name the two exact `PLANS/` output paths and
  forbid `openspec/changes/` as an input or output.
- Build and repair Prompts name the approved Plan files as input and
  `repository.path` as the only checkout to modify.
- Review receives the exact Pull Request head commit and writes the review file
  with exactly one machine line, `Verdict: PASS` or `Verdict: FAIL`, plus
  `Head: <commit>`. A deterministic completion check validates both lines; the
  reviewer's prose or final response is not a second verdict authority.
- Conflict repair receives the failed operation and current base facts. Any
  resulting push invalidates the previous review and returns through the same
  review contract.

### Pull Request Boundary

Build publishes verified output and then creates or reuses one draft Pull
Request. Its identity is stored once as Workflow Runtime Variables so later
Stages address the same external object. Issue title and body remain the source
for Pull Request metadata. The root-level plan files do not enter the branch or
Pull Request unless the coder deliberately creates corresponding Repository
documentation as part of the approved plan.

Plan is the sole default Approval point. Rejection continues the planner Session
with `work.approvalFeedback`, then recaptures both Plan artifacts before another
decision. Check uses the named reviewer's `PASS` or `FAIL` result as its gate and
does not wait for a second Approval.

### Recovery and Invariants

The merge Action enables GitHub Auto-merge with squash and then waits for GitHub
to report `MERGED`. It does not report success merely because Auto-merge was
accepted or checks were pending. The Profile declares rebase, conflict repair,
publish, failed-check repair, and retry work beside the task that understands
the failure.

- Pull Request checks run after reviewer `PASS` and before Integrate. The merge
  Action still observes external state until `MERGED`, so a protection or check
  change after Check cannot bypass the delivery gate.
- A review verdict is valid only for the exact head commit recorded in the
  review file. The Profile records that value as `github.reviewedHead` and
  passes it as `expectedHeadSha` to both required-check and merge Actions. Every
  push clears that binding. Content-changing recovery must cancel queued
  Auto-merge, publish, rerun the reviewer for the new head, and pass required
  checks before Auto-merge can be enabled again.
- After implementation, exact Task IDs, Action inputs, ordering, and recovery
  declarations belong only to the authoritative Profile YAML linked above.
- Every publish and Pull Request side effect is an explicit task. No implicit
  Stage-boundary hook exists.
- `push` has no business recovery. Failure indicates permission, network, or an
  externally modified remote branch and surfaces as ordinary task failure.
- Conflict resolution, check repair, and publishing remain separate work. A
  repair Agent does not silently own a later push.

See [`actions.md`](actions.md) for the Action contract and
[`recovery.md`](recovery.md) for recovery semantics.

## Status

Both bundled YAML definitions still treat the Workspace root as the checkout
and use `workspace.branch`; neither has converged on the shared
`repository.path` and `repository.branch` contract. The bundled `mohist/local`
Profile otherwise remains the implemented OpenSpec-based local flow. The
bundled `mohist/github-pr` YAML still implements its earlier Inline Agent and
OpenSpec design. It does not yet implement the named roles, dedicated Prompt
set, Workspace-level plan files, required durable captures, child Repository
working directory, review-to-head binding, Artifact Store restore, or
Auto-merge completion contract above. Server translation of `mohist/agent` also
drops the engine-reserved `working-directory`, and Runner confinement for that
field is not yet safe against ancestor symbolic links. OpenSpec Actions are not
being removed; the target GitHub Profile simply has no dependency on them.
