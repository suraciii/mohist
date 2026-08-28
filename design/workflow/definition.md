---
status: implemented
---

# Workflow Definition

A Workflow Definition is the core content of a Workflow Profile: a YAML document that declares
stages, tasks, checks, Approval Points, and recovery rules. It is one of the product's command
surfaces. [`docs/workflow-definition.md`](../../docs/workflow-definition.md) is authoritative for
syntax and author-visible semantics. This document defines the semantic model and its single
authoritative validator; it does not repeat the syntax.

## Model

### Semantic Model

The semantic model is separate from its carrier syntax. YAML is parsed into the following types.
The engine, Runner, and validator operate on these types, not on a syntax tree.

```text literal
WorkflowDefinition(ApprovalConfig?, Stages[], Recoveries?)
ApprovalConfig(Feedback: ApprovalFeedbackConfig?)
ApprovalFeedbackConfig(Tasks: Task[])
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
- `ApprovalConfig` and `ApprovalFeedbackConfig` are configuration-only types. Approval Feedback is
  the record submitted to a WorkflowRun; it is not a Definition node.
- Feedback Tasks are an ordered `Task[]`; they do not need a distinct type. The `approval` and
  `feedback` nodes are configuration containers, not domain entities.

### Outside the Model

- Execution state: `recoveryRemaining`, attempts, and task output. These cannot be declared in
  YAML; see [`recovery.md`](recovery.md).
- Variable values and Prompt bodies: these are separate resources, and the model only holds
  `${{ }}` references.
- Profile metadata, including ID, name, and applicable scenario: the Profile resource owns this
  metadata. The Definition top level contains only `approval`, `stages`, and `recoveries`.
- `uses` is always a literal concrete Action name. There is no dynamic or template-driven `uses`
  value, so the semantic model and every runtime consumer contain only concrete Action names.

### Placement

The model, parser, and validator live in the independent `Mohist.Workflow.Definition` library with
no Orleans or ASP.NET dependency. Orleans surrogates remain in Server, following the existing
`WorkflowDefinitionSurrogates` pattern. Both Server and CLI reference this library, so the save API
and local `mo` validation run the same code.

## Semantics

### Approval Feedback

[`docs/concepts.md`](../../docs/concepts.md#approval-point) is authoritative for product behavior.
The `approval.feedback` configuration declares Feedback Tasks for the Approval Feedback record
submitted to a WorkflowRun. `ApprovalConfig` and `ApprovalFeedbackConfig` are configuration-only
implementation types, not additional product concepts.

- Request Changes is available only when the complete Definition bound to the WorkflowRun contains
  a non-empty `approval.feedback.tasks` list.
- Before the first aggregate mutation, WorkflowRun validates the request body, the current Approval
  Point, and the complete declared Feedback Task list. It resolves that list only from the bound
  Definition; it does not read a live Profile or synthesize a default. A validation failure changes
  no Approval Point, Stage, Approval Feedback, Task, Check, WorkflowRun status, event, revision, or
  other aggregate state.
- WorkflowRun owns Approval Point and Approval Feedback state. AgentJob and AgentSession do not.
- Request Changes creates an Approval Feedback record and TaskRun instances from the declared
  Feedback Tasks in order. Mohist does not create a default Task or insert an Agent, prompt, Session,
  timeout, or publication Task.
- After all Feedback Tasks complete, the current Stage Checks run again. The same Approval Point
  then waits for another decision. The original Stage Tasks do not run again.
- The engine does not impose a Request Changes limit.
- A `mohist/agent` Feedback Task follows the ordinary Action contract. It explicitly names its
  Agent and can explicitly name a Session. Named Session reuse requires the same Agent and
  Workspace. AgentJob owns execution, and AgentSession owns conversation continuity.

### Parsing Is Validation

Validation boundary: the Workflow Definition parser owns syntax and semantic validation — parsing
is validation. The compiler lowers a validated definition to the semantic model; runtime tasks
consume the compiled model and never revalidate.

There is one Definition entry point: `Parse(yaml) -> Definition | Error[]`. Profile source first
passes through the Profile parser, which strips Profile metadata, then this entry point receives
ordinary Definition YAML.

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

- Top level: only `approval`, `stages`, and `recoveries` are allowed; `stages` must not be empty.
- `approval.feedback` is optional; when present, `tasks` must not be empty and every item follows
  the task rules.
- A `stage` name must not be empty and must be unique within the Definition; `tasks` must not be
  empty. `lockBehavior` only accepts `sequential` and must appear with non-empty `resources`;
  `resources` cannot appear alone.
- A task `id` must not be empty and must be unique within its task list; `uses` is required;
  `title` is optional.
- `expect`: `files[].path` must not be empty; `markers[].oneOf` must not be empty; `failIf` must be
  a member of `oneOf`.
- `artifacts`: `files[].path` must not be empty.
- `setVars`: the key must not be empty; the value must be an output field path beginning with
  `output.`.
- `recovery`: `budget` must be a non-negative integer; `handlers` must be non-empty and ordered.
- A handler's optional `when` has the form `field=value`, with both sides non-empty; an omitted
  `when` denotes the only default handler and it must be last; at least one of `tasks` or
  `retrySelf` is required.
- A check `id` must not be empty and must be unique within the stage; `uses` is required.
- Every `${{ }}` template must parse; the root namespace must appear in the product reference;
  `failure.*` is allowed only in recovery handler tasks. An ID referenced by `tasks.<id>` must be
  declared in the Definition.
- `with` is omitted or a JSON object; the Definition validator does not interpret internal keys and
  only recursively validates template expressions in values.

Profile validation requires every Agent task to use the literal `mohist/agent` Action with its
`name` and `prompt` inputs, across Stage Tasks, Feedback Tasks, recovery, and deferred task-default
paths.
Runtime Actions such as `mohist/opencode` and `mohist/pi` are rejected in Profile `uses`. These are
Profile composition rules and do not add a dynamic `uses` form to `TaskDefinition`.

### Validation Entry Points

The same implementation is exposed through three entry points, with only one copy of the rules
above:

- Profile save API: rejects an invalid Definition and returns the combined Profile, Definition, and
  Action-contract error list.
- `mo workflow validate --file <path>`: validates locally without resolving a Project or contacting
  Server; `--file -` reads from stdin.
- CI: built-in Profiles and complete examples from `docs/workflow-definition.md` are golden cases
  and must validate. This locks the syntax reference and validator together. Skeleton snippets that
  contain `<...>` placeholders are excluded.

The Profile save entry point combines three non-overlapping decisions. The Profile compiler owns
Profile metadata. The validator in this document owns Definition structure, field types, and the
runtime template language. The Action catalog owns whether a concrete `uses` exists and which
`with` keys, required fields, and types it accepts. All errors use the same YAML path convention
and identify their source, but none repeats another layer's rules. The pure local command runs
Profile composition and Definition validation. The save entry point also validates the materialized
semantic model against the Action catalog.

### Runtime Tasks

`WorkflowDefinition` is not a complete execution plan. WorkflowRun stores the complete validated
Definition that it binds when it starts. Stage initialization, Approval Feedback, named recovery,
and source views read that bound Definition. A later Profile edit affects only future WorkflowRuns.
During a run, Stage Tasks, Feedback Tasks, and named recovery declarations come from the bound
Definition. A control command such as `mo issue rebase` generates its own `uses: mohist/rebase` Task
and may attach recovery selected from the bound Definition. All runtime-created Tasks use the same
dispatch, result, and Variables-resolution contracts.

Runtime-inserted tasks do not pass through Definition validation again. Runner-built recovery
tasks come from a subtree of the already validated bound Definition. The producer of a task built
by a Server control command is responsible for its validity. `mohist/task-list` uses its bound
`task.uses` default for every inserted task and rejects a source task that tries to replace it.

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

- Expansion timing and dispatch input for `with` / `expect`: [`task-dispatch.md`](task-dispatch.md).
- Execution ownership for `expect` / `artifacts` / `setVars` / `error`: [`actions.md`](actions.md).
- Recovery matching, `recoveryRemaining` budget flow, and manual retry reconstruction:
  [`recovery.md`](recovery.md).
- Merge algorithm and write API for `vars.*`: [`variables.md`](variables.md).
- Profile resources, complete Definition binding, and APIs: [`profile.md`](profile.md).
- Built-in Profile tradeoffs and invariants: [`builtin-workflows.md`](builtin-workflows.md).

## Status

The authoritative Definition validator is implemented and shared by the Profile save entry point,
`mo workflow validate --file`, and CI golden cases. The Definition validator owns unknown fields,
field types, `check.id`, required `uses`, and save-time structural validation. The Action catalog
continues to own Action availability and the `with` contract.
