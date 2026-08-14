# Built-in Workflows

The authoritative definitions are
[`mohist-local.workflow.yaml`](../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml)
and
[`mohist-github-pr.workflow.yaml`](../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml).
A `mohist/*` Profile appears in every Project, but the current Mohist version
owns it. An upgrade changes future Stage initialization; it does not rewrite an
initialized Stage or dispatched Task.

A built-in Profile source cannot be modified or deleted. A Project may configure
a binding explicitly declared by that source without copying it. Create a
Project Profile for structural custom behavior. This document records rationale
and invariants without duplicating YAML.

- `mohist/local`: Rebase with squash locally, then push directly to the base
  branch. This is the default.
- `mohist/github-pr`: Deliver through a draft Pull Request, ready transition,
  and squash merge.

Select one with:

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

## Design Drivers

- Local delivery and Pull Request delivery have different shared-boundary risks.
  Keeping two Profiles makes review, authentication, and recovery requirements
  explicit instead of hiding them behind conditional hooks.
- YAML is the single behavioral authority. This document explains why the
  Profiles differ; it does not restate their Task IDs or ordering.
- Every side effect is an explicit Task. Explicit work can be retried, audited,
  and recovered with the same Action contract; an implicit Stage hook cannot.
- A Workspace is rebuildable. When work must survive a Stage or Runner change,
  the remote branch is the recovery boundary and publishing it must be visible
  in the Definition.

## Shared Structure

Both Workflows use the same main path:

```text diagram
plan -> approval -> build -> check -> approval -> integrate
                                                   sequential, with project-integration lock
```

- Every Stage prepares its Workspace explicitly, so no Task relies on a hidden
  directory or branch transition.
- Plan separates proposal, specification, design, tasks, and self-review because
  each artifact supports a different approval question. A failed self-review is
  recoverable work, not a successful plan with a warning.
- Build expands the approved task plan and verifies each increment. Check uses a
  separate review pass so implementation is not its own only acceptance signal.
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

This Profile opens a draft Pull Request after Plan, marks it ready after Check
Approval, and squash-merges during Integrate. Runner host must have an
authenticated `gh` CLI for the target Repository.

The Profile declares `agentAction: mohist/opencode` and uses
`${{ profile.agentAction }}` for every inline Agent task. A Project may bind the
Profile to another compatible concrete Action such as `mohist/pi`. Run creation
fixes that Action for Plan, Build, Check, Integrate, Approval feedback, and
recovery. The versioned Stage graph remains shared and immutable.

Workspace is a rebuildable execution copy. The remote Workflow branch is the
recovery point between Stages. The Pull Request is a review projection of that
branch. Before passing output to another Stage, Approval, or Pull Request
operation, every Repository-modifying Stage explicitly pushes current HEAD to
the Workflow branch. The Profile orders these tasks. Runner only executes and
reports facts. There is no implicit Stage hook.

### Pull Request Boundary

Plan publishes the branch and creates or reuses one draft Pull Request. Its
identity is stored once as Workflow Runtime Variables so later Stages address
the same external review object. Issue title and body remain the source for Pull
Request metadata; copying them into Workflow metadata would create another
authority.

Build publishes verified output because a later Stage may rebuild its Workspace
on another Runner. Check publishes the reviewed result, marks the Pull Request
ready, and verifies external checks. Integrate publishes any final recovery work
and squash-merges the same Pull Request.

Approval feedback is ordered work: the Agent applies feedback, pushes current
HEAD, and then reruns Stage Checks. The Pull Request and recoverable branch
contain feedback output before the next Approval.

### Recovery and Invariants

The merge Action classifies base movement, failed checks, and protection races.
The Profile declares the corresponding rebase, repair, publish, or retry work
beside that Action. This keeps external failure handling visible and prevents a
Stage hook from changing the branch without a Task record.

- Pull Request checks appear at two explicit boundaries. The first runs after
  Check work so a failed external check becomes visible repair work before
  delivery. The second runs immediately before merge so external state that
  changed after Approval cannot bypass the delivery gate; it is an internal
  merge prerequisite, not a Stage Check.
- Exact Task IDs, Action inputs, failure codes, ordering, and recovery behavior
  belong only to the authoritative Profile YAML linked above.
- Every publish and Pull Request side effect is an explicit task. No implicit
  Stage-boundary hook exists.
- `push` has no business recovery. Failure indicates permission, network, or an
  externally modified remote branch and surfaces as ordinary task failure.
- Conflict resolution, check repair, and publishing remain separate work. A
  repair Agent does not silently own a later push.

See [`actions.md`](actions.md) for the Action contract and
[`recovery.md`](recovery.md) for recovery semantics.
