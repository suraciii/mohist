---
status: wip
---

# Prompt Management

A Prompt is a Project-scoped resource in Project Space. WorkflowProfile, Issue, and WorkflowRun do not
own a Prompt or store a Prompt override.

A Project manages its Prompts by key. A builtin `.prompt` file provides a read-only fallback only when the
Project does not configure that key. It is not another configurable scope.

Workflow is one Prompt consumer. A Standalone Agent uses the same Project Prompt collection.

## Resolution

WorkflowProfile stores only a Prompt key reference, such as `${{ prompts.proposal }}`. It does not store
the Prompt body. At dispatch, the Server loads the body by Project and key into the immutable attempt
snapshot. At the execution entry point, the Runner evaluates the body template before it calls the Action:

```text diagram
WorkflowProfile prompts.<key> -- projectId + key --> Prompt Resolver
Project Prompts, key -> body --- configured body ---> Prompt Resolver
Builtin Prompts, key -> body --- fallback on miss --> Prompt Resolver

Prompt Resolver -- load body by key at dispatch --> Attempt Snapshot
Attempt Snapshot -- Runner renders before Action --> Rendered Prompt
```

```text literal
resolvePrompt(projectId, key):
  if Project configured key:
    return Project Prompt body

  return Builtin Prompt body for key
```

Prompts do not merge across scopes and do not produce an `EffectivePrompts` collection. A Prompt body is
one complete string. A Project configuration replaces the entire builtin body for the same key.

A Profile write may validate Prompt key syntax. At dispatch, the Server loads the body from the Project
Prompt or the builtin fallback. If neither source has the key, dispatch fails with an actionable domain
error. The dispatch snapshot freezes the Prompt body for that attempt. A later change does not affect it.

## Rendering

```text literal
PromptTemplateEngine.Render(body, attemptSnapshot)
  ${{ path.to.value }} -> attempt snapshot lookup
```

Rendering uses the Effective Stage Variables and runtime context in the attempt snapshot. It uses the same
template expression syntax as `with` and `expect`. The root namespaces are closed. The task fails if any
expression does not resolve. A value embedded in a string must be a scalar; an object or array fails. When
an expression occupies the whole value, it preserves its JSON type. `\${{` produces a literal `${{`.
Recursive expansion must have a deterministic depth limit. [`workflow/task-dispatch.md`](workflow/task-dispatch.md)
is authoritative for rendering time and attempt snapshot semantics.

The Prompt model has no revision or body-snapshot field. When an attempt snapshot is created at dispatch,
the Server reads the current body from the Project Prompt resource and freezes it in that snapshot.
Redelivery uses the attempt's existing snapshot. Retry and rerun-from-stage use their new attempt snapshots.
Each attempt renders from its own snapshot, so its Prompt body is bound to its dispatch time. After the
Action receives the rendered Prompt, that Action call does not read the Prompt resource again.

Workflow depends only on the Prompt key. The Action receives the rendered Prompt text. It cannot read the
Prompt resource or Variables resource again.

## Builtin Prompt conventions

A builtin `.prompt` is product content. It ships with the product and applies to any project:

- Use English only.
- Keep the content independent of a product repository and technology stack. Do not refer to Mohist's own
  repository commands, directory structure, or examples from its development history.
- The content may refer to Mohist product surfaces, such as the `mo` CLI, documented template namespaces,
  and workflow variables. These surfaces are valid in any managed project. Write the OpenSpec path as
  `openspec/changes/issue-${{ issue.number }}` without an additional namespace dependency.
- State only the task, inputs, outputs, and machine-verifiable contracts such as output paths and markers.
  Do not prescribe process details, problem classifications, or report templates. The executing agent is
  capable, and the primary reader of its report is the agent for the next task.
- A review Prompt diagnoses but does not fix. A separate fix Prompt performs the repair. The review report
  is their handoff surface.
- A Prompt that modifies files, such as a build or fix Prompt, always contains a one-line interruption
  contract. The agent can be interrupted at any time, so it commits as it works and keeps progress records
  current. A review Prompt does not contain this contract because its only artifact is the report. On a
  closeout warning, it immediately finishes the report with the current findings. The Runtime owns the
  injection and text of deadline warnings. See the "Prompt deadlines and two-stage closeout" section in
  [`runtimes/opencode.md`](runtimes/opencode.md). The Prompt does not repeat the warning text.

The same conventions apply to the SKILL.md that ships in the nupkg as CLI skill data.

## API

The Prompt collection is a direct child resource of Project:

```text literal
GET    /api/projects/{projectRef}/prompts
GET    /api/projects/{projectRef}/prompts/{key}
PUT    /api/projects/{projectRef}/prompts/{key}
DELETE /api/projects/{projectRef}/prompts/{key}
POST   /api/projects/{projectRef}/prompts/{key}/preview
```

After a Project Prompt is deleted, its key uses the builtin fallback again. A read fails if no builtin
exists. Issue and WorkflowRun do not provide a Prompt API.

## Status

Gaps from the current implementation:

- The current Project Prompt exposes duplicate paths such as `/templates` and
  `/workflow-profile/prompts`. The target keeps only the Project `/prompts` resource.
- Some current Profile resolution code assembles a Prompt map in advance. The target passes only the key
  and reads one Project Prompt at execution time.
