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
Project { defaultWorkflowProfileId, profileAgentActionOverrides }
  -- owns 1..* --> WorkflowProfile { id, name, description, agentAction?, definition }
  -- default ---> WorkflowProfile

Issue { workflowProfileId? }
  -- belongs to --> Project
  -- selects ----> WorkflowProfile

WorkflowRun { workflowProfileId, agentAction? }
  -- selected at start --> WorkflowProfile

WorkflowProfile: Project-scoped; does not own Variables or Prompts.
WorkflowRun: Profile identity and Agent Action are fixed; Definition resolves as each Stage starts.
```

The minimal `WorkflowProfile` model is:

| Field | Meaning |
|---|---|
| `id` | Stable Profile identifier within a Project |
| `name` | User-facing name |
| `description` | Short description of the applicable scenario |
| `agentAction` | Optional default concrete Action for `${{ profile.agentAction }}` references |
| `definition` | Rules for Stages, initial tasks, checks, Approval, recovery that creates later tasks, and related behavior |

IDs under `mohist/*` are reserved for builtin Profiles that update with the Mohist version. These Profiles
are visible and selectable in the same collection for every Project. They can be the default Profile, but
their source cannot be modified or deleted. A Project may override a builtin Profile's declared
`agentAction`; this changes one Profile binding and does not copy or edit the versioned source. The Project
manages Profiles with other IDs.

A Profile can reference external values through `${{ vars.* }}` and `${{ prompts.* }}`, but it does not
declare or store those values. An Action Input that is fixed and belongs only to one task must be written
directly in `definition`.

## Agent Action Binding

`agentAction` lets one Profile select one concrete inline Agent Action without duplicating its Stage graph.
The Profile source declares the default and the Project may override it for that Profile:

```yaml
id: mohist/github-pr
name: GitHub PR
agentAction: mohist/opencode

stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: ${{ profile.agentAction }}
        with:
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

This is a Profile binding, not a Run Variable. It has deliberately narrow syntax and semantics:

- `${{ profile.agentAction }}` is valid only as the complete scalar value of a `uses` field. It cannot be
  embedded in text, used under `with` or `expect`, or read through another template namespace.
- The effective value is the Project override when present, otherwise the Profile source default. It must
  name a concrete Action whose manifest declares the `agent-turn` capability.
- Profiles that declare `agentAction` use the binding for every inline Agent task, including Approval
  feedback, recovery tasks, and the task default supplied to `mohist/openspec-tasks`. Literal non-Agent
  Actions remain unchanged. Mixing a bound Agent Action with a literal inline Agent Action is invalid.
- Approval feedback that invokes an Agent must declare its task explicitly. Workflow does not synthesize
  an implicit `mohist/opencode` feedback task when the Profile omits one.
- The Profile layer replaces the binding before it creates the `WorkflowDefinition` semantic model and
  before Action-contract validation. `TaskDefinition.Uses`, `TaskRun.Uses`, dispatch, and Runner execution
  therefore always contain a concrete Action such as `mohist/pi`; dynamic `uses` is not part of the
  execution protocol.
- Run creation stores the effective concrete Action beside `workflowProfileId`. Changing the Project
  override later affects only future WorkflowRuns. A later Stage may read updated Profile structure, but
  its Agent references are materialized with the Action already bound to that Run.

A Profile without `${{ profile.agentAction }}` continues to use literal `uses` declarations and does not
need an `agentAction`. Mohist does not provide a general Profile parameter system, conditional Agent task
branches, or a generic Agent-turn Action.

## Agent Runtime Projection

`agentRuntime` is a read-only projection used by Project, Issue, and create-Issue
model selectors. It is not stored in `WorkflowProfile`, is not accepted as
Profile input, and does not participate in execution. The effective concrete
`uses` remains the only Runtime selector for an inline Agent task.

For a bound Profile, the Server derives the projection from the effective
`agentAction`. For a Profile with only literal Action declarations, it derives
the projection from the validated Definition. Both paths use this built-in
mapping:

| Inline Agent Action | Runtime |
|---|---|
| `mohist/opencode` | `opencode` |
| `mohist/pi` | `pi` |

The literal scan includes Stage tasks and checks, Approval feedback tasks,
recovery tasks, and the static `task.uses` default of `mohist/openspec-tasks`.
It follows nested recovery tasks recursively. `mohist/openspec-tasks` does not
permit source tasks to override that default.

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
- Stage-specific selectors for an active Run use that Run's bound Agent Action.
  They do not switch catalogs after a Project override changes.

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
- WorkflowRun stores the Profile ID and effective Agent Action selected at start. A later change to the
  Issue selection, Project default, or Profile Agent Action override affects only future WorkflowRuns. It
  does not switch an active Run to another Profile or Agent Action.
- After the Definition for the same Profile ID changes, an active Run reads the new version when it
  initializes a later Stage.

WorkflowRun does not store a complete Workflow Definition snapshot. Run creation materializes only the
StageRun and Approval facts needed to advance the lifecycle. When each Stage initializes, it uses
`workflowProfileId` to read the Stage structure from the current Profile Definition again. The Provider
materializes Agent references with the Run's bound Action. A Profile edit does not rewrite a Stage that is
already initialized. See [`definition.md`](definition.md) for runtime task creation and insertion.

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

Physical persistence follows the existing ownership boundary. The Project WorkflowProfile settings row
stores one `agentActionOverrides` object keyed by canonical Profile ID; it does not create mutable rows for
builtin Profile source. WorkflowRun state stores only the resolved concrete `agentAction` string. This is a
specific binding, not a general Profile parameter bag.

```text diagram
Issue -> Workflow

WorkflowRun stage initialization -> IWorkflowProfileProvider
                                      ^
                              ProjectWorkflowProfileProvider
```

At WorkflowRun creation, `IWorkflowProfileProvider` resolves the current Profile source and effective
Agent Action. At each Stage initialization it provides the current, validated `WorkflowDefinition` by
Project, Profile ID, and the Run-bound Agent Action. WorkflowRun does not store the Definition body. The
Provider does not read Variables or Prompts and does not select the Profile.

## API

The Profile collection is a child resource of Project:

```text literal
GET    /api/projects/{projectRef}/workflow-profiles
POST   /api/projects/{projectRef}/workflow-profiles
GET    /api/projects/{projectRef}/workflow-profiles/{*profileId}
PUT    /api/projects/{projectRef}/workflow-profiles/{*profileId}
PATCH  /api/projects/{projectRef}/workflow-profiles/{*profileId}
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
`mohist/*` ID. `PUT` or `DELETE` on a builtin Profile must return a domain error. `PATCH` changes only the
Project-scoped Agent Action override and is valid for a builtin Profile that declares `agentAction`:

```json
{ "agentAction": "mohist/pi" }
```

Setting `agentAction` to `null` removes the override and restores the source default. The mutation validates
the materialized Profile against the current Action catalog before committing. An unknown Action, an Action
without `agent-turn`, or an incompatible Action Input contract rejects the mutation without changing the
current binding. If no Action catalog is available, the mutation fails with an actionable validation error;
it never saves an unvalidated structural dispatch choice.

The collection read models expose the effective `agentAction` and nullable derived `agentRuntime`. Project
settings read the effective default and disabled Profile IDs through `/workflow-profile/default`; the Web
client resolves the effective Profile, reads its `agentRuntime`, and requests that Runtime's model catalog.
The Project default mutation uses `PUT /workflow-profile/default` with `{ "profileId": "..." }`.

Settings shows an Agent Action selector only for a Profile that declares the binding. Candidate Actions come
from catalog entries with `agent-turn`; the browser does not infer candidates from names. A successful change
refreshes the Profile read model and its model catalog. Issue and create-Issue screens consume that effective
read model but do not own another Agent Action override.

## Status

Implemented: the Project-scoped WorkflowProfile collection, including builtin `mohist/*` and
Project-managed Profiles; the Project default and explicit Issue selection, including clearing with
`--inherit-workflow-profile`; reference protection on deletion; the ability to change an Issue selection
during an active Run with an effect only on the next Run; a fixed Profile ID for a Run with live Definition
reads at Stage initialization and no Definition snapshot; and separate Variables and Prompts resources.

Implemented: the nullable `agentRuntime` list/detail projection and its use by Project, Issue, create-Issue,
and stage model selectors. The projection is derived recursively from static `uses` declarations and the
browser does not parse Workflow YAML. Runtime-specific model catalogs remain isolated; a missing catalog
or a Profile with no single resolved Runtime does not fall back to another Runtime.

Planned in the Profile Agent Action binding change: `agentAction` source defaults and Project overrides,
the restricted `${{ profile.agentAction }}` compiler, Run-bound concrete Actions, capability-aware catalog
validation, and `mohist/github-pr` adoption. These items must land together before this section becomes
implemented behavior.
