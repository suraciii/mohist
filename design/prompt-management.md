# Prompt Management

A Prompt is a Project-scoped resource in Project Space. WorkflowProfile, Issue,
and WorkflowRun do not own Prompts or store Prompt overrides. A Standalone Agent
uses the same Project Prompt collection.

A Project manages Prompts by key. A builtin `.prompt` file is a read-only
fallback when the Project has no value for that key. It is not another
configuration scope.

## Design Drivers

- Store one complete Prompt body per Project key. Do not merge bodies across
  scopes.
- Bind a Prompt body to an attempt at dispatch. Later edits affect only later
  attempts.
- Keep Prompt resolution independent from WorkflowProfile, Issue, and
  WorkflowRun ownership.
- Use the same closed template namespace and rendering rules as `with` and
  `expect`.
- Keep builtin content portable across managed repositories and technology
  stacks.

## Model

WorkflowProfile stores only a Prompt key reference, such as
`${{ prompts.plan }}`. The Project owns configured Prompt bodies. The product
owns builtin fallback bodies. The attempt snapshot owns the body selected at
dispatch.

```text diagram
 +-----------------+   +-------------------------+   +-------------------------+
 | WorkflowProfile |   | Project Prompts: key -> |   | Builtin Prompts: key -> |
 |  prompts.<key>  |   |          body           |   |          body           |
 +--------+--------+   +------------+------------+   +------------+------------+
          +-------------------------+-+---------------------------+
                                      vprojectId + key
                             +-----------------+
                             | Prompt Resolver |
                             +--------+--------+
                                      |
                                      vload body by key at dispatch
                            +------------------+
                            | Attempt Snapshot |
                            +---------+--------+
                                      |
                                      vRunner renders before Action
                             +-----------------+
                             | Rendered Prompt |
                             +-----------------+
```

At dispatch, Server loads the Project Prompt or builtin fallback by key and
freezes the selected body in the immutable attempt snapshot. If neither source
has the key, dispatch fails with an actionable domain error. The Action receives
only the rendered Prompt text and cannot read Prompt or Variable resources.

The Prompt model has no revision or body-snapshot field. Redelivery reuses the
attempt snapshot. Retry and rerun-from-stage create new attempt snapshots. A
later Prompt change never changes an existing attempt.

## Semantics

### Resolution

A Profile write may validate Prompt key syntax. It does not need the Prompt
body. The Server resolves the body at dispatch. A Project value replaces the
entire builtin body for the same key.

Workflow is one Prompt consumer. The execution entry point loads the same
Project Prompt collection for a Standalone Agent.

### Rendering

```text literal
PromptTemplateEngine.Render(body, attemptSnapshot)
  ${{ path.to.value }} -> attempt snapshot lookup
```

The Runner renders the body before it calls the Action. Rendering uses
Effective Stage Variables and runtime context in the attempt snapshot. Root
namespaces are closed. An unresolved expression fails the task.

A value embedded in a string must be scalar. An object or array fails. An
expression occupying the whole value preserves its JSON type. `\${{` produces a
literal `${{`. Recursive expansion has a deterministic depth limit.
[`workflow/task-dispatch.md`](workflow/task-dispatch.md) defines rendering
time and attempt snapshot semantics.

### Builtin Prompt conventions

A builtin `.prompt` is product content that applies to every Project:

- Use English only.
- Keep content independent of a product repository and technology stack.
- Refer to Mohist surfaces only when they are valid in every managed Project,
  such as the `mo` CLI, documented template namespaces, and Workflow Variables.
- State only task, input, output, and machine-verifiable contract requirements.
  Do not prescribe process details, problem classifications, or report
  templates.
- A review Prompt diagnoses but does not fix. A separate fix Prompt performs
  the repair, and the review report is their handoff.
- A Prompt that modifies files includes a one-line interruption contract. It
  commits as it works and keeps progress records current. A review Prompt does
  not include this contract because its only artifact is the report.
- Runtime owns deadline-warning injection and wording. Prompts do not repeat
  that warning text. See the "Prompt Deadline and Two-Phase Closeout" section
  in [`runtimes/opencode.md`](runtimes/opencode.md).

The same conventions apply to `SKILL.md` shipped as CLI skill data.

### API

The Prompt collection is a direct child resource of Project:

```text literal
GET    /api/projects/{projectRef}/prompts
GET    /api/projects/{projectRef}/prompts/{key}
PUT    /api/projects/{projectRef}/prompts/{key}
DELETE /api/projects/{projectRef}/prompts/{key}
POST   /api/projects/{projectRef}/prompts/{key}/preview
```

Deleting a Project Prompt restores the builtin fallback. A read fails when no
configured or builtin body exists. Issue and WorkflowRun have no Prompt API.

## Status

- The current Project Prompt exposes duplicate paths such as `/templates` and
  `/workflow-profile/prompts`.
- Current Profile resolution code assembles a Prompt map in advance instead of
  passing only the key and reading one Project Prompt at execution time.
