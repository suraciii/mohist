---
status: implemented
---

# Workflow Definition

A Workflow Definition is the core content of a Workflow Profile: a YAML document that declares
stages, tasks, checks, approval points, and recovery rules. It is one of the product's command
surfaces. [`docs/workflow-definition.md`](../../docs/workflow-definition.md) is authoritative for
syntax and author-visible semantics. This document defines the semantic model and its single
authoritative validator; it does not repeat the syntax.

## Model

### Semantic Model

The semantic model is separate from its carrier syntax. YAML is parsed into the following types.
The engine, Runner, and validator operate on these types, not on a syntax tree.

```text literal
WorkflowDefinition(Approval?, Stages[])
Approval(FeedbackTasks: Task[])
Stage(Name, RequiresApproval = false, LockBehavior?, Resources[], Tasks[], Checks[])
Task(Id, Uses, Title?, With?, Expect?, Artifacts?, SetVars?, Recovery?)
Expect(Files[]: FileExpectation(Path),
       Markers[]: MarkerExpectation(Path, OneOf[], FailIf?))
Artifacts(Files[]: ArtifactDeclaration(Path))
Recovery(Budget = 0, Handlers[]: Handler(When, Tasks[], RetrySelf = false))
Check(Id, Uses, Title?, With?)
```

- Every language construct has a corresponding type. `With` is the only open structure: the
  Definition layer only requires a JSON object and recursively validates template expressions in
  it. The selected Action manifest decides which keys are allowed, which are required, and what
  value types they accept.
- `Expect` is a first-class construct at the task level and does not enter `With`. See
  [`actions.md`](actions.md) and [`task-dispatch.md`](task-dispatch.md) for the execution split
  between executor validation and synthesized promise output.
- Approval feedback tasks are an ordered `Task[]`; they do not need a distinct type. The current
  stage's checks run again after all feedback tasks complete.

### Outside the Model

- Execution state: `recoveryRemaining`, attempts, and task output. These cannot be declared in
  YAML; see [`recovery.md`](recovery.md).
- Variable values and Prompt bodies: these are separate resources, and the model only holds
  `${{ }}` references.
- Profile metadata, including ID, name, and applicable scenario: the Profile resource owns this
  metadata. The Definition top level contains only `approval` and `stages`.

### Placement

The model, parser, and validator live in the independent `Mohist.Workflow.Definition` library with
no Orleans or ASP.NET dependency. Orleans surrogates remain in Server, following the existing
`WorkflowDefinitionSurrogates` pattern. Both Server and CLI reference this library, so the save API
and local `mo` validation run the same code.

## Semantics

### Parsing Is Validation

There is one entry point: `Parse(yaml) -> Definition | Error[]`.

- An unknown key is an error, not something to ignore or downgrade to a warning. The Agent's
  generate-validate-repair loop depends on this signal.
- A type mismatch is an error. `budget: abc` reports an error instead of silently taking a default.
- All errors are collected; validation does not stop at the first error.
- Each error consists of a YAML path and a message in domain language, without a stack trace or
  implementation terminology:

```text literal
stages[1].tasks[0].recovery.handlers[0]: handler must declare tasks or retrySelf
```

### Validation Rules

| Location | Rule |
|---|---|
| top level | Only `approval` and `stages` are allowed; `stages` must not be empty |
| approval.feedback | `tasks` must not be empty; every item follows the task rules |
| stage | `stage` name must not be empty and must be unique within the Definition; `tasks` must not be empty |
| stage | `lockBehavior` only accepts `sequential` and must appear with non-empty `resources`; `resources` cannot appear alone |
| task | `id` must not be empty and must be unique within its task list; `uses` is required; `title` is optional |
| expect | `files[].path` must not be empty; `markers[].oneOf` must not be empty; `failIf` must be a member of `oneOf` |
| artifacts | `files[].path` must not be empty |
| setVars | key must not be empty; value must be an output field path beginning with `output.` |
| recovery | `budget` must be a non-negative integer; `handlers` must be non-empty and ordered |
| handler | Optional `when` has the form `field=value`, with both sides non-empty; an omitted `when` denotes the only default handler and it must be last; at least one of `tasks` or `retrySelf` is required |
| check | `id` must not be empty and must be unique within the stage; `uses` is required |
| template | Every `${{ }}` must parse; the root namespace must appear in the product reference table; `failure.*` is allowed only in recovery handler tasks |
| template | An ID referenced by `tasks.<id>` must be declared in the Definition |
| with | Omitted or a JSON object; the Definition validator does not interpret internal keys and only recursively validates template expressions in values |

### Validation Entry Points

The same implementation is exposed through three entry points, with only one copy of the rules
above:

- Profile save API: rejects an invalid Definition and returns the error list.
- `mo workflow validate --file <path>`: validates locally without resolving a Project or contacting
  Server; `--file -` reads from stdin.
- CI: built-in Profiles and complete examples from `docs/workflow-definition.md` are golden cases
  and must validate. This locks the syntax reference and validator together. Skeleton snippets that
  contain `<...>` placeholders are excluded.

The Profile save entry point combines two non-overlapping decisions. The validator in this
document owns Definition structure, field types, and the template language. The Action catalog
owns whether `uses` exists and which `with` keys, required fields, and types it accepts. Both kinds
of errors use the same YAML path convention and identify their source, but neither duplicates the
other's rules. The pure local command runs only Definition validation. The save entry point passes
the parsed semantic model to Action catalog validation.

### Runtime Tasks

`WorkflowDefinition` is not a complete execution plan, and `WorkflowRun` does not store a
Definition snapshot. Run creation materializes the StageRun and approval facts required to advance
the lifecycle. When each Stage initializes, it rereads the current Definition of the selected
Profile. Later edits do not retroactively rewrite an initialized Stage. During a run, recovery,
retry, approval feedback, and control commands such as `mo issue rebase`, which inserts
`uses: mohist/rebase`, may all create new `TaskRun` instances. They use the same dispatch, report,
and Variables resolution semantics.

Runtime-inserted tasks do not pass through Definition validation again. Runner-built recovery
tasks come from a subtree of an already validated Definition. The producer of a task built by a
Server control command is responsible for its validity.

## Examples

Unknown key caused by a typo:

```yaml
handlers:
  - when: error.code=conflict
    retryself: true
```

```text literal
stages[0].tasks[0].recovery.handlers[0]: unknown field retryself; did you mean retrySelf
```

`lockBehavior` without `resources`:

```yaml
- stage: integrate
  lockBehavior: sequential
  tasks: [ ... ]
```

```text literal
stages[1]: lockBehavior requires non-empty resources
```

The Definition validator does not inspect keys inside `with`. Each Action contract owns those
keys; this validator only checks template expressions in their values. The Profile save entry point
can then submit the same task to the Action catalog. A valid Definition therefore does not imply
that the selected Action and its inputs are available in the current Project.

## Implementation Semantics Index

| Construct | Authoritative implementation semantics |
|---|---|
| expansion timing and dispatch input for `with` / `expect` | [`task-dispatch.md`](task-dispatch.md) |
| execution ownership for `expect` / `artifacts` / `setVars` / `error` | [`actions.md`](actions.md) |
| recovery matching, `recoveryRemaining` budget flow, and manual retry reconstruction | [`recovery.md`](recovery.md) |
| merge algorithm and write API for `vars.*` | [`variables.md`](variables.md) |
| Profile resources, live Definition parsing, and APIs | [`profile.md`](profile.md) |
| built-in Profile tradeoffs and invariants | [`builtin-workflows.md`](builtin-workflows.md) |

## Status

The authoritative Definition validator is implemented and shared by the Profile save entry point,
`mo workflow validate --file`, and CI golden cases. The Definition validator owns unknown fields,
field types, `check.id`, required `uses`, and save-time structural validation. The Action catalog
continues to own Action availability and the `with` contract.
