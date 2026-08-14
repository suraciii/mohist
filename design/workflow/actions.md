# Action Design

An Action is the pluggable execution unit of a Workflow task. `uses` selects the Action, `with`
provides all inputs, and the Action returns structured output or an error. For a developer,
authoring an Action means writing a declarative contract, or manifest, plus a pure-function
implementation. The experience follows GitHub Actions.

An Action does not own the Workflow completion decision in `expect`, does not represent an
identifiable Mohist Agent, and is not a separate process. It is a trusted module registered in the
Runner process.

## Model

### Manifest

Each Action declares its contract in a manifest. The manifest is pure data that can be serialized
as JSON. It is defined beside the implementation and gains type inference through `defineAction`:

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

- `name` is the case-insensitive `uses` match key. It is lowercase, has the form
  `<namespace>/<action>`, and has no version segment.
- Every `inputs` entry declares `type` (`string | number | boolean | object | array`), either
  `required` or `default` but not both, and `description`.
- `outputs` declares successful output fields. It is both documentation and a projection contract,
  providing the available paths for `setVars` and `tasks.<id>.outputs.*`.
- `errors` declares every business error code for the Action in kebab-case and its meaning. Recovery
  can match these with `when: error.code=...`, and documentation can be generated from them.

The platform reserves two categories that do not appear in the manifest: the reserved input
`working-directory`, which the engine consumes to select `host.workDir` and which an Action cannot
declare, and the platform error codes `invalid-input`, `unexpected-error`, and `timeout`, which the
engine produces and an Action cannot invent.

### Implementation Interface and Host

`run(inputs, host)` is the entire Action implementation surface. The default host contains only:

```ts
interface ActionHost {
  workDir: string                 // Resolved execution directory
  signal: AbortSignal
  log(source: string, line: string): void
  exec(cmd: string, args: string[], options?): Promise<ExecResult>
}
```

An Action cannot access Run Variables, the Server connection, Runtime handles, recovery
declarations, or dispatch metadata. A Profile must explicitly pass required context through a
`with` template.

### Capabilities

Capabilities beyond the default host must be declared in the manifest. The engine injects only
declared capabilities:

| Capability | Injection | Purpose |
|---|---|---|
| `agent-turn` | `host.agent.execute({ prompt, session?, options? })` | Execute one Agent input. The capability layer owns Session open/attach and the Runtime lifecycle; the Action only expresses intent |
| `add-tasks` | allows a result to carry `addTasks` | Append later tasks. The engine reports them uniformly; the Action does not connect directly to Server |
| `write-vars` | `host.writeVars(vars)` | Persist `vars.*` immediately during execution, unlike the post-completion `setVars` projection. Writes are not rolled back on failure and are visible to retries |

Declaring `agent-turn` also means the Action produces a Runner-private execution fact: the
final assistant text. The capability layer records it so the `_output` marker in `expect` can match
it. The Action result itself does not carry this fact.

### Registry and Catalog

Built-in Runner Actions are registered in one list. The registry is built from manifests and
matches `uses` against `name` case-insensitively. All manifests are collected into a pure-JSON
catalog and reported to Server when Runner registers, together with tombstones for retired Actions.
The catalog preserves manifest capabilities so Server can validate a Profile's `agentAction`
without inferring semantics from an Action name. Each tombstone contains a name and guidance.

Loading external plugins, version segments in `uses` such as `@v1`, and composite Actions that
orchestrate YAML steps are out of scope. The registry retains an extension point that accepts
additional `defineAction` collections.

## Semantics

### Inputs

Actions have one input channel: all inputs come from the rendered and validated `with` value. The
Runner renders and validates against the manifest before calling the Action. The Action cannot see
raw `with`, a Variables resource, or dispatch context. [`task-dispatch.md`](task-dispatch.md) is
authoritative for rendering timing and attempt snapshot semantics.

```yaml
- id: integrate:rebase
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

The Runner execution entry point applies this order. Failure at any step prevents the Action call:

1. Clone the original `with` from the attempt snapshot. Preserve fields declared
   `render: deferred` by the manifest and recursively expand `${{ ... }}` in every other field. An
   unresolved reference fails under the dispatch contract; see
   [`task-dispatch.md`](task-dispatch.md).
2. Render `expect` from the same attempt snapshot. The result remains a Workflow-owned completion
   contract and does not enter the Action input channel.
3. Validate rendered inputs against the manifest. An unknown input key fails with `invalid-input`
   instead of being silently ignored.
4. A missing `required` input fails with `invalid-input`.
5. A type mismatch fails with `invalid-input`.
6. Apply each `default`; `run` receives complete, strongly typed inputs.

A field declared `render: deferred` is excluded from recursive expansion in step 1 and passes
unchanged through steps 3-6. Internal `${{ ... }}` expressions remain available for an Action that
must propagate them into later tasks. Other object and array fields use the normal recursive rules.

Consistency constraints between inputs, such as merge preconditions, belong to the Action's
semantics and are checked at the beginning of `run`. A failure returns an error code declared by
the manifest or `invalid-input`.

### Results

An Action has exactly one of two public result shapes:

```json
{ "output": { "prNumber": 42, "prUrl": "https://github.com/example/repo/pull/42" } }
```

```json
{ "error": { "code": "pr-checks-failed", "message": "PR #42 checks failed. Fix the failures and retry." } }
```

- `output` is a JSON object or `null` and stays structured end to end. Runner internals, the report
  wire format, `TaskRun.Output` storage, `setVars` projection, `tasks.<id>.outputs.*` reads, and
  recovery matching through `when: output.*` all operate on the same object. No layer stringifies
  and reparses it.
- `error.code` must be a code declared in the manifest's `errors` or a platform code.
- `error.message` is the only user-visible error text. An error has no additional details; raw
  command output and diagnostics belong in the task log.
- Native exceptions are not part of the protocol. The engine normalizes them to `unexpected-error`
  at the Action boundary.
- An Action with the `add-tasks` capability may include `addTasks` in a successful result, which the
  engine reports.
- The engine does not validate successful output against a schema at runtime. If a field declared
  in `outputs` is missing, `setVars` projection exposes an explicit error.

`TaskRun.Output` stores only successful Action output. If `expect`, a workspace constraint, or
another Runner postcondition fails after an Action succeeds, TaskRun can store both the original
output and the Runner-produced error. Task status, exit code, and Runner-private execution facts
belong to the task execution protocol, not the public Action result.

Generic result handling in the engine does not interpret any Action's business semantics. The only
capability-based branch is for an Action that declares `agent-turn`: the task executor projects
its output from `expect` as `null | { promise }`, as described below. Every other Action preserves
its output unchanged. There is no special-case list based on `uses`.

### Validation Timing and Catalog Consumption

- **Profile save or update:** Server performs full validation against the most recently reported
  catalog. Unknown `uses`, unknown input keys, missing `required` inputs, and constant input type
  mismatches are actionable errors. Inputs containing template expressions are checked only for
  key names; type validation waits for the Runner execution entry point. If no catalog has been
  reported, this layer is skipped and recorded without blocking a Profile that uses only literal
  Actions. A Profile that declares `agentAction`, or a mutation of its Project override, is rejected
  when no catalog is available because the capability and complete materialized contract cannot be
  validated.
- **Runner execution entry point, authoritative and fail-closed:** Runner renders the original
  `with` from the attempt snapshot, enforces its local manifest, and fails the task with
  `invalid-input` instead of invoking `run` with unvalidated input. Server no longer expands
  templates before dispatch.
- **Retired Action:** a tombstone encountered during Runner rendering fails with its guidance; a
  tombstone encountered during Profile save rejects the save.

Profile save first materializes the Profile's optional Agent Action binding, then uses the Workflow
Definition validator to produce a semantic model and the catalog to evaluate concrete `uses` and
`with`. When `agentAction` is present, the catalog also requires that Action to declare `agent-turn`.
For an update referenced by active bound Runs, Server repeats materialization and Action-contract validation
once per distinct bound Action as well as for the future effective Action. The Project-scoped Profile
reference coordinator serializes this validation and write with new Run bindings.
The Definition validator only recursively checks runtime template expressions in `with` values. The
catalog does not repeat Profile binding, Definition field, or template namespace validation. All
diagnostic sources are combined into one validation exception, use the same YAML path convention,
and carry source labels. A successful save response explicitly contains
`actionValidation: { performed, reason? }`, telling the caller whether Action-contract validation
ran. If the catalog is unavailable, the response explains the skipped validation. Built-in Profile
loading, runtime loading, and `mo run validate` only perform Definition validation and do not depend
on the catalog. Legacy `with.agent`, `with.kind`, `with.type`, and `with.expect` receive no special
treatment and are rejected as unknown input keys.

### `setVars`

`setVars` projects Action output fields into Run Variables:

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

- The left side is a path under `vars`; the right side is a JSON path in Action output.
- Runner applies `setVars` before reporting task completion. A projection failure, including a
  missing path, fails the task instead of being silently skipped.
- Only `vars.*` can change. `workflow`, `stage`, `work`, `issue`, and `workspace` cannot.
- A recovery task can overwrite the same `vars.*` path.
- Runner uses the same Run Variables PATCH API as other callers, but its generated body contains
  only `vars`, not `stages`. See [`variables.md`](variables.md) for complete semantics.

### `artifacts`

`artifacts` declares output to collect. Collection is best-effort: a missing file is skipped and
does not fail the task.

```yaml
artifacts:
  files:
    - path: docs/proposal.md
```

### `expect`

`expect` is a Workflow-owned task completion contract separate from Action input. See the
[product reference](../../docs/workflow-definition.md#expect-completion-requirements) for
author-visible failure rules and its relationship with `artifacts`. The Runner task executor
renders `expect` against the attempt snapshot and applies completion checks only after the Action
succeeds. If the Action fails, is cancelled, or times out, the original failure is preserved and no
file or marker is read. Neither the Action nor its capability implementation interprets `expect`,
and rendered `expect` does not enter the Action input channel.

A marker `path` may use the special value `_output`, which matches the final assistant text of this
execution instead of file content. The task executor obtains that text from the execution fact
recorded by the `agent-turn` capability. It does not enter Action output and requires no extra
Action declaration.

`_output` recognizes only the promise-tag form `<promise>VALUE</promise>`. If multiple accepted
values appear, the last occurrence in the text wins, unlike file markers where declaration order
wins. To match a literal substring of final assistant text, encode the literal as the `VALUE`
inside a promise tag in `oneOf`. `_output` does not read the file system, and evidence collection
does not treat it as a file path.

### Errors and Recovery

Runner constructs an `{ output, error }` context for recovery. An explicit `when` uses a path in
that context:

```yaml
handlers:
  - when: error.code=rebase-conflict
  - when: output.promise=FAIL
```

The final handler without `when` is a fallback only when an error exists. It can handle a failure
that the executor finds after Action completion, such as a dirty workspace. `error.message` is not
a machine protocol and must not be used in `when`.

There is no global Action error enum, and the engine does not understand error-specific meaning.
Each Action manifest is the authoritative catalog of its error codes. See
[`recovery.md`](recovery.md) for recovery design.

### Checks

A Stage check uses the same `uses` and `with` values to reuse the same Action contract. The check
host maps success or failure to pass or fail. An Action does not know whether it is running as a
task or a check.

## Built-In Actions

### `mohist/opencode`

This Runtime-specific Action declares `agent-turn`. See
[`../agent-execution.md`](../agent-execution.md) for invariants about its ownership relationship
with Agent and Session, including that direct use means an Inline Agent and does not resolve an
Agent definition. Because `uses` already selects the Runtime, inputs need no `kind` or `type`
discriminator.

Input contract:

```ts
type OpenCodeActionInput = {
  prompt: string                    // Non-empty string rendered by Runner
  session?: string                  // Logical Session name
  options?: {
    model?: string                  // provider/model; model itself may contain '/'
    variant?: string                // Separate sibling of model; never joined to the model ID
  }
}
```

`options` is normally expanded as a whole value from `${{ vars.agent }}`. See
[`task-dispatch.md`](task-dispatch.md) for template evaluation timing. Keys other than `model` and
`variant` in `options` are ignored and recorded in diagnostics without failing execution. Workflow
provides `expect` separately as the task completion contract. Legacy `with.expect` and `with.agent`
are rejected with actionable errors when the Profile loads.

Output contract:

```ts
type OpenCodeActionOutput = null | { promise: string }
```

The task executor synthesizes this `{ promise }` output for an `agent-turn` Action based on
`expect`; neither the Action nor the capability layer produces it. Runtime Session identity, model,
usage, transcript, diagnostics, and expectation details stay in the models that own them and are
not copied into Action output. See [`../runtimes/opencode.md`](../runtimes/opencode.md) for the
OpenCode implementation.

`mohist/pi` is a peer concrete Action with the same Profile-facing input shape and `agent-turn`
capability. A parameterized Profile still materializes one of these concrete names before Action
validation and dispatch; there is no generic Agent-turn Action in the registry.

### `mohist/openspec-tasks`

`mohist/openspec-tasks` appends planned Build tasks through its `add-tasks` capability. Its
`task.uses` input is a required default for every appended task. A Profile may set this value to
`${{ profile.agentAction }}` because Profile materialization runs before deferred Action Input is
captured.

The source `tasks.json` may describe task identity, title, prompt context, and completion
expectations, but it cannot provide `uses`. Allowing a source task to replace `task.uses` would
bypass Profile validation and could mix Agent Runtimes inside a Run. The Action rejects such input
with `invalid-input`; it does not fall back to `mohist/opencode`.

### Git and GitHub PR Actions

`mohist/push`, `create-github-pr`, `mark-github-pr-ready`, `merge-github-pr`, and `mohist/rebase`
are ordinary Workflow Actions with one input channel. A Profile explicitly passes the base branch,
workflow branch, remote, and other context through `${{ repository.* }}` and
`${{ workspace.* }}`. An Action does not look up Run Variables itself.

- `push` is solely responsible for publishing the current workspace's committed HEAD to the remote
  workflow branch. One WorkflowRun exclusively owns the workflow branch, so the Profile uses a
  force update and does not depend on a remote-tracking ref.
- `create-github-pr` only creates or updates a draft PR and outputs a stable PR identity. It performs
  no Git operation and does not decide which commit should be published.
- `mark-github-pr-ready` marks a draft PR ready and is idempotent if it is already ready.
- `merge-github-pr` squash-merges a PR and must wait for PR checks before the merge.

Publishing, PR metadata, and merging are three independent tasks with independent failure
boundaries. A push failure retries only push; a PR operation failure retries only that PR operation;
merge recovery handles only its own failure.

Waiting for PR checks is an internal precondition of the merge Action, not a Stage-level check. It
polls `gh pr view --json statusCheckRollup`. If checks are empty, it waits within a 120-second grace
window. Failed checks return `error.code: pr-checks-failed`. The Action performs no implicit repair;
the Profile must declare explicit recovery.

`mohist/github-pr-checks` exposes the same polling and classification logic as an explicit task in
the Stage graph. A typical Profile places this delivery CI check after `mark-pr-ready` in the check
Stage. It reuses the merge Action's polling pure function and `pr-checks-failed` error code, so its
recovery handler is symmetric with merge-pr: the same `recover:fix-pr-checks` + `recover:push` +
`retrySelf`. It is read-only: it does not modify the PR, push, or perform implicit repair. The
Profile declares recovery explicitly.

See [`builtin-workflows.md`](builtin-workflows.md) for the complete task graph.

## Status

Implemented: manifests and `defineAction`; registry and catalog reporting, including tombstones,
when Runner registers with Server; catalog validation during Profile save with an
`actionValidation` response marker; declarative capability injection for `agent-turn`,
`add-tasks`, and `write-vars`; structured output end to end; and `setVars` projection. Named
special-case lists, `PROMISE_PROJECTED` and `REMOVED`, have been removed. Privileged access such as
`openspec-tasks` has been reduced to declarative effects.

Implemented in the Profile Agent Action binding change: capability projection in the Server catalog,
capability validation for `agentAction`, and the required non-overridable `task.uses` contract for
`mohist/openspec-tasks`.
