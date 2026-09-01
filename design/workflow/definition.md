# Workflow Definition

A Workflow Definition is the YAML body of a Workflow Profile. It declares
stages, tasks, checks, Approval Points, and recovery rules. The product
reference [`docs/workflow-definition.md`](../../docs/workflow-definition.md)
owns syntax and author-visible behavior. This document owns the semantic model
and its single authoritative validator.

## Design Drivers

- YAML syntax and the semantic model remain separate. Runtime code consumes the
  compiled model, not a syntax tree.
- Server and CLI use one parser and validator. Save-time and local validation
  must not drift.
- Validation is fail-closed and collects every actionable error with a YAML
  path and domain-language message.
- A WorkflowRun binds one complete validated Definition. Later Profile edits
  affect only future runs.
- Validation layers have separate ownership. Definition validation does not
  repeat Profile or Action-catalog rules.

## Model

### Semantic Model

YAML is parsed into these types:

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

Every language construct has a corresponding type. `With` is the only open
structure. The Definition layer requires a JSON object and recursively checks
template expressions in it. The selected Action manifest decides which keys
are allowed, required, and type-checked.

`Expect` is a task-level construct and never enters `With`. See
[`actions.md`](actions.md) and [`task-dispatch.md`](task-dispatch.md) for the
split between executor validation and synthesized promise output.

`ApprovalConfig` and `ApprovalFeedbackConfig` are configuration-only types.
Approval Feedback is a WorkflowRun record, not a Definition node. Feedback
Tasks are an ordered `Task[]`; `approval` and `feedback` are containers, not
domain entities.

### Outside the Model

The Definition does not contain:

- Execution state such as `recoveryRemaining`, attempts, or task output. See
  [`recovery.md`](recovery.md).
- Variable values or Prompt bodies. It contains only `${{ }}` references to
  those resources.
- Profile metadata such as ID, name, or applicable scenario. The Profile owns
  that metadata.
- Dynamic `uses` values. `uses` is always a literal concrete Action name.

The Definition top level contains only `approval`, `stages`, and `recoveries`.

## System Boundary

The parser and validator live in the independent
`Mohist.Workflow.Definition` library without Orleans or ASP.NET dependencies.
The Profile parser strips Profile metadata before calling
`Parse(yaml) -> Definition | Error[]`. Server and CLI reference the same
library. Orleans surrogates remain in Server through the existing
`WorkflowDefinitionSurrogates` pattern.

## Semantics

### Approval Feedback

[`docs/concepts.md`](../../docs/concepts.md#approval-point) is authoritative
for product behavior. `approval.feedback` declares Feedback Tasks for the
Approval Feedback record submitted to a WorkflowRun. The configuration types
are not additional product concepts.

- Request Changes is available only when the bound Definition contains a
  non-empty `approval.feedback.tasks` list.
- Before the first aggregate mutation, WorkflowRun validates the request body,
  current Approval Point, and complete declared Feedback Task list. It resolves
  the list only from the bound Definition. It does not read a live Profile or
  synthesize a default.
- A validation failure changes no Approval Point, Stage, Approval Feedback,
  Task, Check, WorkflowRun status, event, revision, or other aggregate state.
- WorkflowRun owns Approval Point and Approval Feedback state. AgentJob and
  AgentSession do not.
- Request Changes creates an Approval Feedback record and TaskRun instances
  from the declared Feedback Tasks in order. Mohist does not add a default
  Task, Agent, Prompt, Session, timeout, or publication Task.
- After all Feedback Tasks complete, the current Stage Checks run again. The
  same Approval Point then waits for another decision. Original Stage Tasks do
  not run again.
- The engine imposes no Request Changes limit.
- A `mohist/agent` Feedback Task follows the ordinary Action contract. It names
  its Agent and may name a Session. Named Session reuse requires the same Agent
  and Workspace. AgentJob owns execution, and AgentSession owns continuity.

### Parsing Is Validation

Parsing is validation. The Definition parser owns syntax and semantic
validation. The compiler lowers a validated Definition to the semantic model,
and runtime tasks never revalidate it.

There is one Definition entry point: `Parse(yaml) -> Definition | Error[]`.
The entry point must:

- Report unknown keys as errors, not warnings or ignored fields.
- Report type mismatches as errors. For example, `budget: abc` must not take a
  default.
- Collect all errors instead of stopping at the first error.
- Report each error as a YAML path and domain-language message without a stack
  trace or implementation terminology:

```text literal
stages[1].tasks[0].recovery.handlers[0]: handler must declare tasks or retrySelf
```

### Validation Rules

- The top level allows only `approval`, `stages`, and `recoveries`. `stages`
  must not be empty.
- `approval.feedback` is optional. When present, `tasks` must not be empty and
  every item follows the task rules.
- A Stage name must not be empty and must be unique. `tasks` must not be empty.
  `lockBehavior` accepts only `sequential` and requires non-empty `resources`.
  `resources` cannot appear alone.
- A task `id` must not be empty and must be unique within its task list. `uses`
  is required and `title` is optional.
- `expect.files[].path` must not be empty. `expect.markers[].oneOf` must not be
  empty, and `failIf` must be a member of `oneOf`.
- `artifacts.files[].path` must not be empty.
- A `setVars` key must not be empty. Its value must be an output field path
  beginning with `output.`.
- `recovery.budget` must be a non-negative integer. `handlers` must be
  non-empty and ordered.
- A handler's optional `when` has the form `field=value`, with both sides
  non-empty. An omitted `when` is the only default handler and must be last.
  At least one of `tasks` or `retrySelf` is required.
- A check `id` must not be empty and must be unique within its Stage. `uses` is
  required.
- Every `${{ }}` template must parse. Its root namespace must appear in the
  product reference. `failure.*` is allowed only in recovery handler tasks.
  Every `tasks.<id>` reference must name a declared task.
- `with` is omitted or a JSON object. The Definition validator does not
  interpret its internal keys and only checks template expressions in values.

Across Stage Tasks, Feedback Tasks, recovery, and deferred task-default paths,
every Agent task must use the literal `mohist/agent` Action with `name` and
`prompt` inputs. Runtime Actions such as `mohist/opencode` and `mohist/pi` are
rejected in Profile `uses`. These are Profile composition rules and do not add
a dynamic `uses` form to `TaskDefinition`.

### Validation Entry Points

The same implementation is exposed through three entry points:

- **Profile save API:** rejects an invalid Definition and returns the combined
  Profile, Definition, and Action-contract errors.
- **`mo workflow validate --file <path>`:** validates locally without resolving
  a Project or contacting Server. `--file -` reads stdin.
- **CI:** validates built-in Profiles and complete examples from
  `docs/workflow-definition.md` as golden cases. Snippets containing `<...>`
  placeholders are excluded.

The Profile save entry point combines three non-overlapping decisions:

- The Profile compiler owns Profile metadata.
- This validator owns Definition structure, field types, and the runtime
  template language.
- The Action catalog owns concrete `uses` availability and its `with` keys,
  required fields, and types.

All errors use the same YAML path convention and identify their source. None
repeats another layer's rules. The local command runs Profile composition and
Definition validation. Save also validates the materialized model against the
Action catalog. A successful save response states whether Action validation ran
with `actionValidation: { performed, reason? }`.

Built-in Profile loading and runtime loading perform Definition validation
without the catalog. Legacy `with.agent`, `with.kind`, `with.type`, and
`with.expect` are rejected as unknown input keys.

### Runtime Tasks

`WorkflowRun` stores the complete validated Definition that it binds when it
starts. Stage initialization, Approval Feedback, named recovery, and source
views read that bound Definition. A later Profile edit affects future runs
only.

During a run, Stage Tasks, Feedback Tasks, and named recovery declarations
come from the bound Definition. A control command such as `mo issue rebase`
generates its own `uses: mohist/rebase` Task and may attach recovery selected
from the bound Definition. All runtime-created Tasks use the same dispatch,
result, and Variables-resolution contracts.

Runtime-inserted tasks do not pass through Definition validation again. Runner
recovery tasks come from an already validated subtree. A Server control command
producer is responsible for the validity of its generated task.
`mohist/task-list` uses its bound `task.uses` default for every inserted task
and rejects a source task that tries to replace it.

### Contract Ownership

- Expansion timing and dispatch input for `with` and `expect`:
  [`task-dispatch.md`](task-dispatch.md).
- Execution ownership for `expect`, `artifacts`, `setVars`, and `error`:
  [`actions.md`](actions.md).
- Recovery matching, `recoveryRemaining`, and manual retry reconstruction:
  [`recovery.md`](recovery.md).
- Merge algorithm and write API for `vars.*`:
  [`variables.md`](variables.md).
- Profile resources, complete Definition binding, and APIs:
  [`profile.md`](profile.md).
- Built-in Profile tradeoffs and invariants:
  [`builtin-workflows.md`](builtin-workflows.md).

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

The Definition validator does not inspect keys inside `with`. Each Action
contract owns those keys. This validator checks only template expressions in
values. Profile save can then submit the task to the Action catalog, so a valid
Definition does not imply that its Action or inputs are available in the
current Project.

## Status

The authoritative Definition validator is implemented and shared by Profile
save, `mo workflow validate --file`, and CI golden cases. It owns unknown
fields, field types, `check.id`, required `uses`, and structural validation.
The Action catalog owns Action availability and the `with` contract.
