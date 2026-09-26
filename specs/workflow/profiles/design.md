# Workflow Profile

`WorkflowProfile` is a Project-scoped resource that defines how an Issue moves from Draft to Done. A
Project may own multiple Profiles and select one as its default. An Issue inherits that default
or selects another Profile from the same Project.

A Profile contains Workflow structure and behavior only. Variables and Prompts are separate
resources. See [`variables.md`](../variables/spec.md), [`../prompt-management.md`](../../project-space/prompts/spec.md), and [`actions.md`](../actions/design.md) for their contracts.

## Design Drivers

- A Profile must describe one complete Workflow structure without owning runtime state, Variable
  values, or Prompt bodies.
- A WorkflowRun must remain stable when a Project default, Issue selection, or Profile
  Definition changes.
- Profile selection must have one linearization point so redelivery cannot bind a newer Profile
  accidentally.
- Built-in Profiles must be available without copying or mutating their source.
- Agent execution configuration belongs to the Agent. A Task selects an Agent but does not
  override its execution definition.
- Profile validation and binding must use one complete Definition. No active Run reads a live
  Profile to fill missing behavior.

## Model

```text literal
Project { defaultWorkflowProfileId }
  -- owns 1..* --> WorkflowProfile { id, name, description, definition }
  -- default ---> WorkflowProfile

Issue { workflowProfileId? }
  -- belongs to --> Project
  -- selects ----> WorkflowProfile

WorkflowRun { workflowProfileId, definition }
  -- selected and bound at start --> WorkflowProfile

WorkflowProfile: Project-scoped; does not own Variables or Prompts.
WorkflowRun: Profile ID, complete Definition, and verification command bind at start.
```

The minimal Profile model has four fields:

- `id`: a stable identifier within the Project.
- `name`: a user-facing name.
- `description`: the applicable scenario in short form.
- `definition`: validated rules for Stages, Tasks, Checks, Approval Feedback, recovery, and related
  behavior.

WorkflowRun stores the selected Profile ID and the complete validated Definition effective at
binding. That Definition is immutable for the Run. The Run also stores the Project's
deterministic verification command when a built-in Profile uses it.

A Profile may reference `${{ vars.* }}` and `${{ prompts.* }}`, but it does not declare or store those values.
An Action Input fixed to one Task belongs directly in `definition`.

IDs under `mohist/*` are reserved for built-in Profiles. Mohist exposes these Profiles in every
Project. Their source cannot be modified or deleted, and a release update affects only future
WorkflowRuns. A Project manages Profiles with other IDs.

## Semantics

### Agent Task Binding

An executable Agent Task uses the literal `mohist/agent` Action with a named Agent. The Profile
supplies `name`, `prompt`, and optional `session` and `timeout` inputs. Mohist does not
insert omitted inputs.

The Agent definition owns Runtime, Model, Reasoning Effort, Variant, and Skills. The Task cannot
override them. Named Session reuse requires the same Agent and Workspace. AgentJob owns
execution, AgentSession owns conversation continuity, and WorkflowRun owns Approval Point state.

Feedback Tasks, recovery Tasks, and the `mohist/task-list` default use the same literal binding.
`${{ profile.agentAction }}` and Project Agent Action overrides do not exist. Mechanical Feedback Tasks use their
ordinary Actions.

Built-in Profiles reference the Project's single deterministic verification command as
`${{ workflow.verification.command }}`. This is not a Variable. The command is read while a WorkflowRun binds and is copied
into the Run startup facts. Built-in local and GitHub PR Profiles execute it as one `core/script`
Task from `REPOS/${{ repository.name }}` with the built-in timeout and recovery contract. A custom WorkflowProfile
owns multiple verification boundaries.

### Selection and Binding

An Issue start request captures its explicit Profile selection before durable delivery creates
the WorkflowRun. The coordinator resolves the effective selection at the Run-binding
linearization point:

```text literal
selectedProfileId =
  startRequest.workflowProfileId ?? project.defaultWorkflowProfileId
```

The binding rules are:

- The Project default must reference a Profile owned by that Project.
- An explicit Issue selection must reference a Profile in the same Project.
- Clearing the explicit Issue selection restores inheritance from the Project default.
- Profiles do not inherit from or merge with one another. The selection is one complete Profile.
- Binding captures the selected Profile ID and complete validated Definition in one payload.
- A later Issue selection, Project default, or Profile Definition change affects only future
  WorkflowRuns.
- The bound Definition controls every Stage, Approval Feedback behavior, and recovery selection
  in the Run. `mo run view --yaml` reads this Definition.
- A completed or stopped WorkflowRun is an immutable terminal record. Retry, rerun,
  rerun-from-stage, and resume reject it. Starting work again creates a new WorkflowRun ID and
  binds again.

Variables and Prompt bodies are not copied into the bound Definition. At dispatch, Server
resolves Effective Stage Variables and loads Prompt bodies into the immutable attempt snapshot.
A later Variable or Prompt edit can affect only a Task that has not dispatched. It cannot change
a dispatched attempt. [`task-dispatch.md`](../execution/design.md) owns the evaluation timing.

### Ownership

`WorkflowProfile` belongs to the Workflow core domain, with `ProjectId` as its tenancy boundary. Project
holds the default Profile reference. Issue holds an optional explicit Profile reference.
WorkflowRun owns its bound complete Definition.

The coordinator and provider own the binding boundary:

```text diagram
                                                once              +------------------+
                                               +----------------->| Profile Provider |
+-------+                   +-----------------+|                  +------------------+
| Start +------------------>| Resolve Profile ++
+-------+                   +-----------------+|bound definition  +-------------+
                                               +----------------->| WorkflowRun |
                                                                  +-------------+
```

The provider participates only in Profile management and Run binding. An active WorkflowRun does
not read it for Stage, Approval Feedback, recovery, or source-view behavior.

Start binding follows these invariants:

- A Run start has stable Project, Issue, Epic, explicit Profile, metadata, Workspace, and Run
  identity.
- The coordinator captures the selected Profile ID and complete Definition before participant
  delivery.
- The participant creates the Run with the complete binding in one transaction. It never patches
  a partially created Run.
- Redelivery uses the captured payload. It cannot resolve a newer Project default or Profile
  Definition.
- A replay with matching startup facts returns the stored binding. Conflicting facts are
  rejected.
- The coordinator order is the linearization point. A concurrent Profile edit is entirely before
  or after Run binding.

### API

The Profile collection is a child resource of Project:

`/api/projects/{projectRef}/workflow-profiles`

The Project's `defaultWorkflowProfileId` and the Issue's `workflowProfileId` reference this collection. The Project and
Issue resources modify those references. Profile deletion protects a Profile referenced by a
default, an Issue, or an active WorkflowRun.

Updating a Definition with the same ID is allowed. Server validates the new Definition and its
Action contracts before writing it. The update does not inspect or change active WorkflowRuns.

`profileId` is a terminal catch-all, so it can address IDs such as `mohist/local` without loss.
Variables and Prompts use separate APIs. They are not children of `/workflow-profiles/{*profileId}`.

`GET` and collection-list responses include built-in and Project-managed Profiles. `POST`
rejects a `mohist/*` ID. `PUT` and `DELETE` on a built-in Profile return a domain error.
There is no Profile Agent Action override mutation.

Collection read models expose Profile identity and structure. Project settings read the
effective default through `/workflow-profile/default`. The default mutation uses `PUT /workflow-profile/default` with `{ "profileId": "..." }`.

`GET /api/workflow-runs/{workflowRunId}` exposes `workflowProfileId` beside Run status. The Run YAML read returns the complete bound
Definition. Task views expose `agentJobId` and `agentSessionId` for Agent-backed Tasks.

## Status

Current gaps for complete Definition binding and Approval Feedback are recorded once in
[Core Concepts: Approval Point](spec.md#implementation-gaps-1).
## Built-in Workflows

The built-in Profiles are authoritative in [`mohist-local.workflow.yaml`](../../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-local.workflow.yaml) and [`mohist-github-pr.workflow.yaml`](../../../packages/server/src/Mohist.Server/Workflow/Services/Profiles/mohist-github-pr.workflow.yaml). Mohist exposes each
`mohist/*` Profile in every Project, but its source belongs to the current Mohist release. An
upgrade changes only WorkflowRuns started after the upgrade.

A built-in Profile cannot be edited or deleted. A Project may configure a binding explicitly
declared by that source without copying it. Structural changes require a Project Profile. This
document records the built-in delivery boundaries and invariants. The YAML remains the authority
for task IDs, ordering, inputs, and recovery declarations.

The available built-in Profiles are:

- `mohist/local`: Rebase with squash locally, then push directly to the base branch. This is the
  default.
- `mohist/github-pr`: Deliver through a draft Pull Request, ready transition, and auto-merge.

Select a Profile with:

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

### Core Decisions

- The two delivery modes remain separate Profiles because local delivery and Pull Request
  delivery have different review, authentication, and recovery boundaries.
- Every side effect is an explicit Task. A Stage boundary has no hidden hook.
- YAML is the single behavioral authority. This document explains the delivery choices without
  copying YAML structure.
- A Workflow Workspace is rebuildable. The remote Workflow branch recovers Repository work for
  the Pull Request Profile. Plan material is Workspace-local and is regenerated by `mo run rerun --from-stage plan`
  after loss.
- `PLANS/tasks.json` is the only planning artifact consumed as machine input. Other planning material
  remains Agent organization. See [`plan-artifacts.md`](../execution/design.md).
- Every built-in Stage prepares its Workspace explicitly. No Task relies on an implicit
  directory or branch transition.
- Plan, Build, Check, and Integrate use one shared path. Plan and Check stop at Approval Points.
  Build and Integrate advance automatically unless a failure intervenes.
- Recovery is declared beside the Task whose failure it understands. Rebase conflicts and check
  failures use explicit recovery Tasks.

### System Boundary

Both Profiles implement this path:

```text diagram
                                                                      +--------------------------+
+------+    +----------+    +-------+    +-------+    +----------+    |  integrate: sequential,  |
| plan +--->| approval +--->| build +--->| check +--->| approval +--->| with project-integration |
+------+    +----------+    +-------+    +-------+    +----------+    |           lock           |
                                                                      +--------------------------+
```

#### Shared Structure

- Plan produces `PLAN.md`, `DESIGN.md`, and `tasks.json` in one Agent Session. It has no self-review
  Task. The Plan Approval Point is the review boundary.
- Build expands the approved task list into ordered Agent Tasks and verifies each increment.
- Check records independent review evidence and leaves the verdict to the approver. See
  [`plan-artifacts.md`](../execution/design.md).
- Approval Feedback follows [`definition.md`](../definition/design.md#approval-feedback). Built-in Agent Feedback Tasks explicitly use `mohist/agent`,
  the `mohist/builder` Agent, and `feedback-${{ stage.name }}` as their Session name. Mechanical Feedback Tasks remain
  ordinary explicit Tasks.
- Agent work runs from the Workspace root so `PLANS/` and `REPOS/` remain in scope.
  Repository-only Actions select `REPOS/<repository-name>` explicitly. Runner enforces branch and clean-worktree
  invariants at the Repository boundary in either directory.
- The built-in verification boundary is one ordinary `core/script` Task. It runs the Project's
  `verificationCommand` from `REPOS/${{ repository.name }}` with a 900000 ms timeout. The command is frozen when the WorkflowRun
  binds, so Project edits cannot change a retry or later Task in that Run. A custom Profile owns
  multiple independent verification boundaries.

See [`recovery.md`](../execution/design.md) for recovery semantics and [`actions.md`](../actions/design.md) for Action contracts.

#### Local Delivery

`mohist/local` has no GitHub dependency or Pull Request. Check verifies that the Issue branch can
merge before its Approval Point because no remote Approval Point proves mergeability. Integrate
then rebases and squashes onto the current base branch and pushes it.

Repository health checks remain explicit Tasks. Their recovery is limited to the formatting or
patch problem they detect. It is not a general repair hook.

A push has no business recovery. A failure indicates permission, network, or an externally
modified remote branch and surfaces as ordinary Task failure.

#### Pull Request Delivery

`mohist/github-pr` opens a draft Pull Request after Plan, marks it ready after approval at Check, and
enables auto-merge during Integrate. The target Repository must allow auto-merge, and the Runner
host must have an authenticated `gh` CLI.

Every Agent Task in this Profile uses `mohist/agent` with a named built-in Agent. The same binding
applies to Plan, Build, Check, Feedback, and recovery Tasks. Mechanical publication Tasks use
their ordinary Actions. The bound Definition fixes the Stage graph for the WorkflowRun.

The Repository checkout lives at `REPOS/<repository-name>/` and is the only tree that enters Git. Plan and review
material lives under `PLANS/`. The remote Workflow branch is the recovery point for Repository
work. Plan material is uploaded as run artifacts and is not pushed.

Before output crosses a Repository-modifying Stage, an Approval Point, or a Pull Request
operation, that Stage explicitly pushes the current HEAD to the Workflow branch. Runner executes
and reports the publish Task. No Stage hook publishes implicitly.

Plan creates or reuses one draft Pull Request after publishing the branch. WorkflowRun records
the Pull Request number once as its write-once review identity. The first `github.pr.number` carrier that
reaches the WorkflowRun records the number. Repeating the same number is idempotent. A
conflicting number is rejected before dispatch. The Profile passes `vars.github.pr.number` to later Pull
Request Actions and retains `vars.github.pr.url` for presentation.

Issue title and body remain the source for Pull Request metadata. Copying them into Workflow
metadata would create another authority. Build publishes verified output. Check publishes the
reviewed result, marks the Pull Request ready, and verifies external checks. Integrate enables
auto-merge on the same Pull Request and waits for GitHub to perform the merge.

The Profile declares ordered Feedback Tasks for Agent changes and the later push. Feedback
output reaches the remote branch before the Run returns to the same Approval Point.

#### Pull Request Recovery

Auto-merge moves merge arbitration to GitHub. The synchronous merge's base-movement and
protection-race branches no longer exist.

The registration Action classifies a required check that fails after approval as `pr-checks-failed` and
uses the same declared fix-and-push recovery as Check. It classifies a merge conflict as
`conflict` and uses rebase recovery. Enabling auto-merge on a Repository that disallows it is an
ordinary Task failure, `auto-merge-unavailable`.

One auto-merge attempt has a fixed 30-minute absolute deadline. The deadline covers prechecks,
Pull Request reads, subject selection, registration mutation, ambiguous-mutation reconciliation,
polling, and retry delays. An explicit subject wins. Otherwise the Action uses the title from
its bounded Pull Request read. `retry-safe` permits a later explicit retry. It does not authorize
unattended Profile recovery. Host cancellation remains cancellation.

Pull Request checks appear at two explicit boundaries. Check exposes a failed external check as
repair work before delivery. Integrate waits for checks again because external state may change
after approval. A failed check cannot silently bypass the delivery Approval Point.

`push` has no business recovery. Conflict resolution, check repair, and publishing remain
separate work. A repair Agent never silently owns a later push.

### Non-Goals

- This document does not duplicate exact Task IDs, Action inputs, failure codes, ordering, or
  recovery declarations from the Profile YAML.
- A built-in Profile does not provide a hidden Stage hook or an implicit side effect.
- A Project needing structural changes does not mutate a built-in source. It creates a Project
  Profile.
- A custom Profile with several independent verification boundaries is outside the built-in
  verification contract.

### Status

The built-in data and Pull Request boundaries are implemented. The local setup performs explicit
verification, rebase, squash, and push Tasks. The Pull Request setup performs explicit branch
publication, draft creation, ready transition, auto-merge, and post-merge verification.

The current build still contains one Manager conversation gap: Manager requests are acknowledged
with a text message and use a retired model-output management protocol. The target boundary uses
the ordinary command surface and standard liveness projection instead.
