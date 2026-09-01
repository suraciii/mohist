# Workflow Profile

`WorkflowProfile` is a Project-scoped resource that defines how an Issue moves from Draft to Done. A
Project may own multiple Profiles and select one as its default. An Issue inherits that default
or selects another Profile from the same Project.

A Profile contains Workflow structure and behavior only. Variables and Prompts are separate
resources. See [`variables.md`](variables.md), [`../prompt-management.md`](../prompt-management.md), and [`actions.md`](actions.md) for their contracts.

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
a dispatched attempt. [`task-dispatch.md`](task-dispatch.md) owns the evaluation timing.

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
[Core Concepts: Approval Point](../../docs/concepts.md#implementation-gaps).
