---
status: implemented
---

# Workflow Profile

`WorkflowProfile` is a Project-scoped resource that defines how an Issue moves from Draft to Done.
A Project can own multiple Profiles and select one as its default Profile. An Issue can inherit the
default Profile or explicitly select another Profile in the same Project.

A Profile contains only Workflow structure and behavior. It does not contain Variables or Prompts.
See [`variables.md`](variables.md) for Variable resolution,
[`../prompt-management.md`](../prompt-management.md) for Prompt resolution, and
[`actions.md`](actions.md) for the Action contract.

## Model

```text diagram
Project { defaultWorkflowProfileId }
  -- owns 1..* --> WorkflowProfile { id, name, description, definition }
  -- default ---> WorkflowProfile

Issue { workflowProfileId? }
  -- belongs to --> Project
  -- selects ----> WorkflowProfile

WorkflowRun { workflowProfileId }
  -- selected at start --> WorkflowProfile

WorkflowProfile: Project-scoped; does not own Variables or Prompts.
WorkflowRun: Profile identity is fixed; Definition resolves as each Stage starts.
```

The minimal `WorkflowProfile` model is:

| Field | Meaning |
|---|---|
| `id` | Stable Profile identifier within a Project |
| `name` | User-facing name |
| `description` | Short description of the applicable scenario |
| `definition` | Rules for Stages, initial tasks, checks, Approval, recovery that creates later tasks, and related behavior |

IDs under `mohist/*` are reserved for builtin Profiles that update with the Mohist version. These Profiles
are visible and selectable in the same collection for every Project. They can be the default Profile, but
they cannot be modified or deleted. The Project manages Profiles with other IDs.

A Profile can reference external values through `${{ vars.* }}` and `${{ prompts.* }}`, but it does not
declare or store those values. An Action Input that is fixed and belongs only to one task must be written
directly in `definition`.

## Agent Runtime Projection

`agentRuntime` is a read-only projection used by Project, Issue, and create-Issue
model selectors. It is not stored in `WorkflowProfile`, is not accepted in
Workflow YAML, and does not participate in execution. `uses` remains the only
Runtime selector for an inline Agent task.

The Server derives the projection from the validated Definition with this
built-in mapping:

| Inline Agent Action | Runtime |
|---|---|
| `mohist/opencode` | `opencode` |
| `mohist/pi` | `pi` |

The scan includes Stage tasks and checks, Approval feedback tasks, recovery
tasks, and the static `task.uses` default of `mohist/openspec-tasks`. It follows
nested recovery tasks recursively. Runtime-created task overrides are execution
data and do not change the Profile projection.

The result is deliberately small:

```text literal
agentRuntime = exactly one discovered Runtime ? that Runtime : null
```

- A Profile with no inline Agent Action has `agentRuntime: null`.
- A Profile that statically selects more than one Runtime has
  `agentRuntime: null`.
- `mohist/agent` does not contribute a Runtime because its named Agent
  definition owns Runtime, model, and variant selection.
- A null projection does not make the Profile invalid. It means Mohist cannot
  offer one shared Workflow model selector for that Profile.

The Project WorkflowProfile list and detail read models expose `agentRuntime`.
The browser consumes that field and never parses Profile YAML or infers a
Runtime from a model ID. A newly supported built-in Runtime adds one row to the
mapping; it does not add a Profile field, Action input, or Runtime descriptor.

The model selector resolves the Profile through the same selection rule as a
future WorkflowRun:

- Project settings use the Project's effective default Profile.
- Issue settings use the Issue's explicit Profile, or the Project's effective
  default when the Issue inherits it.
- Create Issue uses the Profile currently selected in the form.
- Stage-specific selectors use the same Profile Runtime while writing the
  existing Stage-specific `vars.agent` override.

After resolving the Profile, the selector requests that Runtime's existing
model catalog. It never falls back to another Runtime. Changing a Profile does
not rewrite or clear Project or Issue Variables. A configured model that is no
longer present in discovery remains visible and can be changed or cleared
explicitly.

## Selection

An Issue makes one selection when it starts a WorkflowRun:

```text literal
selectedProfileId =
  issue.workflowProfileId ?? project.defaultWorkflowProfileId
```

- The Project default must reference a Profile that the Project owns.
- An explicit Issue selection must also reference a Profile in the same Project.
- After the explicit Issue selection is cleared, the Issue inherits the Project default again.
- Profiles do not inherit from or merge with each other. The selection is always one complete Profile.
- WorkflowRun stores the Profile ID selected at start. A later change to the Issue selection or Project
  default affects only future WorkflowRuns. It does not switch an active Run to another Profile.
- After the Definition for the same Profile ID changes, an active Run reads the new version when it
  initializes a later Stage.

WorkflowRun does not store a complete Workflow Definition snapshot. Run creation materializes only the
StageRun and Approval facts needed to advance the lifecycle. When each Stage initializes, it uses
`workflowProfileId` to read the Stage structure from the current Profile Definition again. A Profile edit
does not rewrite a Stage that is already initialized. See [`definition.md`](definition.md) for runtime task
creation and insertion.

[`task-dispatch.md`](task-dispatch.md) is authoritative for Variable and Prompt evaluation time. At
dispatch, the Server resolves Effective Stage Variables for the current Stage and loads each Prompt body by
key. It sends them to the Runner with the original `with` and `expect` declarations as an immutable attempt
snapshot. At the execution entry point, the Runner renders the original `with` and `expect` from that
snapshot before it calls the Action. A dispatched attempt does not read the latest Variables or Prompt
again. Its snapshot remains unchanged for the lifetime of that attempt.

## Ownership

`WorkflowProfile` belongs to the Workflow core domain, with `ProjectId` as its tenancy boundary. Project
holds the default Profile reference. Issue holds an optional explicit Profile reference. Neither copies the
Profile body.

```text diagram
Issue -> Workflow

WorkflowRun stage initialization -> IWorkflowProfileProvider
                                      ^
                              ProjectWorkflowProfileProvider
```

At WorkflowRun creation and each Stage initialization, `IWorkflowProfileProvider` provides the current,
validated `WorkflowDefinition` by Project and Profile ID. WorkflowRun does not store the Definition body.
The Provider does not read Variables or Prompts and does not select the Profile.

## API

The Profile collection is a child resource of Project:

```text literal
GET    /api/projects/{projectRef}/workflow-profiles
POST   /api/projects/{projectRef}/workflow-profiles
GET    /api/projects/{projectRef}/workflow-profiles/{*profileId}
PUT    /api/projects/{projectRef}/workflow-profiles/{*profileId}
DELETE /api/projects/{projectRef}/workflow-profiles/{*profileId}
```

The Project's `defaultWorkflowProfileId` and the Issue's `workflowProfileId` reference this collection.
They are modified through the Project and Issue resources, respectively. Profile deletion must protect a
Profile that is still referenced by a default, an Issue, or an active WorkflowRun. Updating the Definition
while keeping the same ID is allowed. An active WorkflowRun reads the new version at a later Stage
initialization.

`profileId` is a terminal catch-all, so it can address an ID such as `mohist/local` without loss. Variables
and Prompts use separate APIs. They are not children of `/workflow-profiles/{*profileId}`.

`GET` and the collection list return both builtin and Project-managed Profiles. `POST` does not accept a
`mohist/*` ID. `PUT` or `DELETE` on a builtin Profile must return a domain error.

## Status

Implemented: the Project-scoped WorkflowProfile collection, including builtin `mohist/*` and
Project-managed Profiles; the Project default and explicit Issue selection, including clearing with
`--inherit-workflow-profile`; reference protection on deletion; the ability to change an Issue selection
during an active Run with an effect only on the next Run; a fixed Profile ID for a Run with live Definition
reads at Stage initialization and no Definition snapshot; and separate Variables and Prompts resources.

Not yet implemented: the `agentRuntime` list/detail projection and its use by
Project, Issue, and create-Issue model selectors.
