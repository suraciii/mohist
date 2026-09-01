# Action Design

An Action is the pluggable execution unit of a Workflow task. `uses` selects
an Action, `with` supplies its inputs, and the Action returns structured output
or an error. An Action is a trusted module registered in the Runner process,
not a separate process or an identifiable Mohist Agent.

Workflow owns the completion contract in `expect`. Action capabilities and
results do not replace that contract. See
[`../decisions/workflow-agent-binding.md`](../decisions/workflow-agent-binding.md)
for the `mohist/agent` binding and [`runner.md`](../runner.md#report) for the
report boundary.

## Core Decisions

- The manifest is the only Action contract. It declares inputs, outputs,
  business errors, and capabilities beside the implementation.
- Runner renders and validates `with` before calling `run(inputs, host)`.
  Actions never read raw `with`, Run Variables, Server state, or dispatch
  metadata.
- Action results stay structured from Runner through `TaskRun.Output`,
  `setVars`, output references, and recovery matching.
- Capabilities are explicit. The engine injects only capabilities declared by
  the manifest.
- The registry reports a manifest catalog and tombstones to Server. Profile
  validation uses that catalog without inferring behavior from an Action name.

## System Boundary

```text diagram
+----------------+   uses, with, expect   +------------------+
| Workflow task  |------------------------>| Runner executor  |
| Workflow-owned |                         +--------+---------+
+----------------+                                  |
                                      render, validate |
                                                      v
                                             +----------------+
                                             | Action.run     |
                                             | inputs, host   |
                                             +--------+-------+
                                                      |
                                                      v
                                             +----------------+
                                             | output or error|
                                             +----------------+
```

An Action receives only the rendered `with` value and the declared host. It
cannot own Workflow completion, access a Server connection, choose recovery,
or inspect a Runtime handle. A Profile passes required context through `with`.

### Manifest

Each Action declares a pure-data manifest that can be serialized as JSON. The
manifest sits beside the implementation and gains type inference through
`defineAction`:

```ts
export const rebaseAction = defineAction({
  name: "mohist/rebase",
  description: "Rebase the workflow branch onto the base branch",
  inputs: {
    baseBranch: { type: "string", required: true },
    remote: { type: "string", default: "origin" },
  },
  outputs: {
    headSha: { type: "string", description: "HEAD after the rebase" },
  },
  errors: {
    "rebase-conflict": "The rebase produced conflicts that require manual or recovery work",
  },
  run: async (inputs, host) => {
    // inputs is validated and defaults are applied:
    // { baseBranch: string; remote: string }
    const result = await host.exec("git", ["rebase", `${inputs.remote}/${inputs.baseBranch}`])
    if (!result.ok) return err("rebase-conflict", result.stderr)
    return ok({ headSha: result.stdout.trim() })
  },
})
```

Manifest rules:

- `name` is the case-insensitive `uses` key. It must be lowercase, use the
  `<namespace>/<action>` form, and have no version segment.
- Each input declares a type from `string | number | boolean | object | array`,
  exactly one of `required` or `default`, and a description.
- `outputs` declares successful fields. It documents the output and provides
  the paths available to `setVars` and `tasks.<id>.outputs.*`.
- `errors` declares every business error code in lowercase kebab-case and its
  meaning. Recovery may match a declared code with `when: error.code=...`.

The engine reserves `working-directory` as an input. It consumes that value to
select `host.workDir`, so an Action cannot declare it. The engine also owns the
platform errors `invalid-input`, `unexpected-error`, and `timeout`.

`host.workDir` is the Action execution directory. For a Workflow Workspace,
Runner separately derives the target Repository guard directory from Workspace
and Repository facts. Branch stability and clean-worktree checks use that guard
directory even when `host.workDir` is the Workspace root.

### Implementation Surface

`run(inputs, host)` is the complete Action implementation surface. The default
host is:

```ts
interface ActionHost {
  workDir: string                 // Resolved execution directory
  signal: AbortSignal
  log(source: string, line: string): void
  exec(cmd: string, args: string[], options?): Promise<ExecResult>
}
```

### Capabilities

Capabilities beyond the default host must be declared in the manifest. The
engine injects only declared capabilities:

- `agent-turn` injects `host.agent.execute({ prompt, session?, options? })`.
  The capability layer opens or attaches the Session and owns the Runtime
  lifecycle. The Action expresses intent for one Agent input.
- `add-tasks` allows a successful result to carry `addTasks`. The engine reports
  the tasks uniformly; the Action does not connect to Server.
- `write-vars` injects `host.writeVars(vars)`. It persists `vars.*` during
  execution, unlike post-completion `setVars`. These writes are not rolled back
  on failure and are visible to retries.

An `agent-turn` Action also produces a Runner-private final assistant-text
fact. The capability layer records that fact so `expect` can match `_output`.
The fact is not part of Action output.

### Registry and Catalog

Built-in Runner Actions are registered in one list. The registry matches
`uses` against manifest `name` case-insensitively. Runner reports all manifests
as a pure-JSON catalog when it registers with Server, together with tombstones
for retired Actions. The catalog includes capabilities so Server can validate
Profile `uses` declarations without inferring semantics from a name. Each
tombstone contains a name and guidance.

The registry has an extension point for additional `defineAction` collections.
External plugins, versioned `uses` such as `@v1`, and composite Actions that
orchestrate YAML steps are not supported.

## Execution Semantics

### Inputs

Actions have one input channel: the rendered and validated `with` value. The
Runner renders and validates it against the manifest before calling the Action.
[`task-dispatch.md`](task-dispatch.md) owns rendering timing and attempt
snapshot semantics.

```text diagram
+------------------+
| Attempt snapshot |
+--------+---------+
         |
         v
+------------------+       +----------------+
| Render with      |------>| Render expect  |
+--------+---------+       +--------+-------+
         |                          |
         v                          v
+------------------+       +----------------+
| Validate inputs  |       | Workflow check |
+--------+---------+       +----------------+
         |
         v
+------------------+
| Apply defaults   |
+--------+---------+
         |
         v
+------------------+
| Action.run       |
+------------------+
```

Failure at any input step prevents the Action call:

1. Clone `with` from the attempt snapshot. Preserve fields declared
   `render: deferred` by the manifest and recursively expand `${{ ... }}` in
   every other field. An unresolved reference fails under the dispatch
   contract; see [`task-dispatch.md`](task-dispatch.md).
2. Render `expect` from the same attempt snapshot. It remains a Workflow-owned
   completion contract and never enters the Action input channel.
3. Validate rendered inputs against the manifest. An unknown input key fails
   with `invalid-input` instead of being ignored.
4. A missing required input fails with `invalid-input`.
5. A type mismatch fails with `invalid-input`.
6. Apply each default. `run` receives complete, strongly typed inputs.

A field declared `render: deferred` is excluded from recursive expansion in
step 1 and passes unchanged through steps 3 to 6. Internal `${{ ... }}`
expressions remain available for an Action that propagates them to later tasks.
Other object and array fields use normal recursive rules.

Consistency constraints such as merge preconditions belong to Action semantics
and are checked at the beginning of `run`. The Action returns a manifest error
code or `invalid-input`.

### Results

An Action has exactly one of these public result shapes:

```json
{ "output": { "prNumber": 42, "prUrl": "https://github.com/example/repo/pull/42" } }
```

```json
{ "error": { "code": "pr-checks-failed", "message": "PR #42 checks failed. Fix the failures and retry." } }
```

- `output` is a JSON object or `null` and stays structured end to end. Runner
  internals, the report wire format, `TaskRun.Output`, `setVars`,
  `tasks.<id>.outputs.*`, and recovery matching through `when: output.*` use
  the same object. No layer stringifies and reparses it.
- `error.code` must be declared by the manifest or be a platform code.
- `error.message` is the only user-visible error text. Raw command output and
  diagnostics belong in the task log.
- Native exceptions normalize to `unexpected-error` at the Action boundary.
- An Action with `add-tasks` may include `addTasks` in a successful result.
- The engine does not validate successful output against an output schema. A
  missing field is reported as an explicit `setVars` projection error.

The engine maps a successful task Action to `completed` and an Action error to
`failed`. Timeout and uncertain execution use `timeout` and `unknown`. It does
not emit `success`, `ok`, or `succeeded`. A checks batch uses `pass` or `fail`.
The report boundary and binding rules are authoritative in
[`../runner.md`](../runner.md#report).

`TaskRun.Output` stores only successful Action output. If `expect`, a Workspace
constraint, or another Runner postcondition fails after an Action succeeds,
TaskRun may store both the original output and the Runner error. Task status,
exit code, and Runner-private execution facts belong to the task protocol, not
the public Action result.

Generic result handling does not interpret Action business semantics. The only
capability-based branch is `agent-turn`: the executor projects its `expect`
result as `null | { promise }`, as described below. Every other Action
preserves its output unchanged. There is no `uses`-based special-case list.

### Validation Timing and Catalog Consumption

- **Profile save or update:** Server validates against the most recently
  reported catalog. Unknown `uses`, unknown input keys, missing required inputs,
  and constant input type mismatches are actionable errors. Template values are
  checked for key names only; type validation waits for Runner execution. If no
  catalog exists, catalog validation is skipped and recorded. A Profile that
  uses only literal Actions is not blocked by that absence.
- **Runner execution:** Runner is authoritative and fail-closed. It renders the
  original `with` from the attempt snapshot, enforces the local manifest, and
  fails with `invalid-input` before invoking `run`. Server no longer expands
  templates before dispatch.
- **Retired Action:** A tombstone fails Runner rendering with its guidance and
  rejects Profile save.

Profile save uses the Definition validator for the semantic model and the
catalog for concrete `uses` and `with`. Agent tasks use the server-side
`mohist/agent` boundary and are validated as a virtual manifest. Runtime
Actions such as `mohist/opencode` and `mohist/pi` are rejected in Profile
`uses` and are invoked only by the AgentJob launcher.

The Definition validator recursively checks template expressions in `with`
values. The catalog does not repeat Profile, Definition-field, or template
namespace validation. Diagnostic sources are combined into one validation
exception, use one YAML path convention, and carry source labels. A successful
save response contains `actionValidation: { performed, reason? }`. Built-in
Profile loading, runtime loading, and `mo workflow validate --file` perform
Definition validation without the catalog. Legacy `with.agent`, `with.kind`,
`with.type`, and `with.expect` are rejected as unknown input keys.

### `setVars`

`setVars` projects Action output fields into Run Variables:

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

- The left side is a path under `vars`; the right side is a JSON path in Action
  output.
- Runner applies `setVars` before reporting task completion. A projection
  failure, including a missing path, fails the task.
- Only `vars.*` can change. `workflow`, `stage`, `work`, `issue`, and
  `workspace` cannot change.
- A recovery task may overwrite the same `vars.*` path.
- Runner uses the Run Variables PATCH API with a body containing only `vars`.
  See [`variables.md`](variables.md) for complete semantics.

### `artifacts`

`artifacts` declares output to collect. Collection is best-effort. A missing
file is skipped and does not fail the task.

```yaml
artifacts:
  files:
    - path: PLANS/PLAN.md
```

### `expect`

`expect` is a Workflow-owned task completion contract separate from Action
input. See the
[product reference](../../docs/workflow-definition.md#expect-completion-requirements)
for author-visible failure rules and its relationship with `artifacts`. The
Runner renders `expect` from the attempt snapshot and applies completion checks
only after the Action succeeds. If the Action fails, is cancelled, or times
out, the original failure is preserved and no file or marker is read. Neither
the Action nor its capabilities interprets `expect`.

A marker `path` may be `_output`, which matches the final assistant text of an
execution instead of file content. The executor obtains that text from the
`agent-turn` fact. It does not enter Action output and requires no extra
manifest declaration.

`_output` recognizes only `<promise>VALUE</promise>`. If multiple accepted
values appear, the last occurrence wins. File markers use declaration order.
To match a literal substring of final assistant text, encode the literal as the
`VALUE` inside a promise tag in `oneOf`. `_output` does not read the file system,
and evidence collection does not treat it as a file path.

### Errors and Recovery

Runner constructs an `{ output, error }` context for recovery. An explicit
`when` uses a path in that context:

```yaml
handlers:
  - when: error.code=rebase-conflict
  - when: output.promise=FAIL
```

The final handler without `when` is a fallback only when an error exists. It
can handle a post-Action failure such as a dirty Workspace. `error.message` is
not a machine protocol and must not be used in `when`.

There is no global Action error enum. Each manifest is authoritative for its
codes. A Runtime or resolver may use another diagnostic vocabulary, but the
Action boundary maps it to a declared lowercase kebab-case code. An undeclared
non-platform code becomes `unexpected-error`. See
[`recovery.md`](recovery.md) for recovery design.

### Checks

A Stage check uses the same `uses` and `with` values and the same Action
contract. The check host maps success or failure to `pass` or `fail`. An Action
does not know whether it runs as a task or a check.

## Built-In Actions

### `mohist/opencode` and `mohist/pi`

These Runtime-specific Actions declare `agent-turn`. They are internal
Agent-to-Runner contracts. The AgentJob launcher invokes the Action for an
Agent whose definition selects the Runtime. A Workflow Profile cannot bind
these names directly and must use `mohist/agent`.

The launcher supplies this input shape:

```ts
type OpenCodeActionInput = {
  prompt: PromptSpec                // String or structured prompt spec; Runner renders it to non-empty text
  session?: string                  // Logical Session name
  options?: {
    model?: string                  // provider/model; model itself may contain '/'
    variant?: string                // Separate sibling of model; never joined to the model ID
  }
}
```

The launcher supplies `options` from the Agent definition. Keys other than
`model` and `variant` are ignored and recorded in diagnostics. Workflow
provides `expect` separately. Because the launcher knows the Runtime, these
inputs have no `kind` or `type` discriminator.

The executor synthesizes this output from `expect`:

```ts
type OpenCodeActionOutput = null | { promise: string }
```

The Action and capability layer do not produce that output. Runtime Session
identity, model, usage, transcript, diagnostics, and expectation details stay
in their owning models. See [`../runtimes/opencode.md`](../runtimes/opencode.md)
for the OpenCode implementation. `mohist/pi` has the same launcher-facing
shape and capability.

There is no generic Agent-turn Action in the registry. Workflow Profiles bind
Agent work through `mohist/agent` only.

### `mohist/task-list`

`mohist/task-list` appends planned Build tasks through `add-tasks`. Its
`task.uses` input is a required literal Action default for every appended task,
typically `mohist/agent` with a fixed Agent `name`.

The source task list may describe task identity, title, goal, acceptance
criteria, and plan references, but it cannot provide `uses`. Replacing
`task.uses` would bypass Profile validation and mix execution identities inside
a Run. The Action rejects that input with `invalid-input`.

The task list is a Workspace-local file. Rebuilding a Workspace loses it, and
recovery reruns the plan Stage to regenerate it. See
[`plan-artifacts.md`](plan-artifacts.md) for the persistence boundary.

### Git and GitHub PR Actions

`mohist/push`, `create-github-pr`, `mark-github-pr-ready`,
`enable-github-pr-auto-merge`, and `mohist/rebase` are ordinary Workflow
Actions with one input channel. A Profile passes repository and Workspace
context through `${{ repository.* }}` and `${{ workspace.* }}`. An Action does
not read Run Variables itself.

- `push` publishes the current committed HEAD to the remote Workflow branch.
  One WorkflowRun exclusively owns that branch, so the Profile uses a force
  update and does not depend on a remote-tracking ref.
- `create-github-pr` creates or updates a draft PR and outputs a stable PR
  identity. It performs no Git operation and does not choose the commit.
- `mark-github-pr-ready` marks a draft PR ready and is idempotent when it is
  already ready.
- `enable-github-pr-auto-merge` registers the merge method, then waits for
  GitHub to merge the PR. It uses the same bounded polling and classification as
  `mohist/github-pr-checks` and is idempotent when auto-merge is enabled.
  One attempt has one fixed 30-minute deadline. Every external command, retry
  delay, registration reconciliation, and poll consumes the remaining budget.
  An explicit squash subject takes precedence. Without one, the Action uses
  the PR title from its bounded PR read and does not read a separate Issue
  field. Failed checks return `error.code: pr-checks-failed`; merge conflicts
  return `conflict`. The Profile declares recovery explicitly.

`retry-safe` is a failure classification, not retry authority. A new explicit
attempt may repeat the operation after the current task fails, but a Profile
does not automatically retry it. Host cancellation remains platform
cancellation and must not become `retry-safe`.

Publishing, PR metadata, and merge registration are independent tasks with
independent failure boundaries. A push failure retries only push. A PR
operation failure retries only that PR operation.

The `mohist/github-pr-status` Stage Check with `expect: merged` is one-shot
post-hoc verification. The registration Action has already completed its wait.
The `mohist/github-pr-checks` Stage Check exposes polling and classification as
an explicit task. A typical Profile places it after `mark-pr-ready` in the
check Stage. Failed checks return `error.code: pr-checks-failed`, and the
Profile declares `recover:fix-pr-checks` with `recover:push` and `retrySelf`.
The check is read-only: it does not modify the PR, push, or perform implicit
repair. See [`builtin-workflows.md`](builtin-workflows.md) for the complete
task graph.

## Non-Goals

- External plugins, versioned Action names, and composite Actions are not
  supported.
- A Workflow Profile cannot invoke Runtime-specific Actions directly.
- An Action does not own Workflow state, recovery state, or Agent execution
  lifecycle.

## Status

Implemented: manifests and `defineAction`; registry and catalog reporting,
including tombstones; catalog validation during Profile save with an
`actionValidation` response marker; declarative `agent-turn`, `add-tasks`, and
`write-vars` capabilities; structured output; `setVars` projection; catalog
capability projection in Server; and the non-overridable `task.uses` contract
for `mohist/task-list`.
