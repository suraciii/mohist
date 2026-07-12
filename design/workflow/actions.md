# Action Design

Action = workflow task execution interface. `uses` selects the action. `with` passes input. Runner executes, reports result + output.

## Boundaries

- Action defines its own input and output.
- Engine never maintains a unified action output schema.
- Engine never defines global `FailureKind` / `ErrorKind`.
- Engine never interprets action output semantics.

Engine only: expands task input and Workflow-owned completion declarations, stores task
output, projects output to workflow variables via `setVars`, matches `when` for recovery,
and inserts recovery tasks mechanically.

## Input

```yaml
- id: integrate:rebase
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

Workflow expands templates. Never interprets `baseBranch`, `remote` business meaning.

## Output

`TaskRun.Output` = `JsonElement?`. Action's full JSON output, stored as-is.

```json
{
  "errorCode": "base-moved",
  "message": "PR not mergeable",
  "prNumber": 42
}
```

Fields like `errorCode` are the action's own interface, not platform enums.

Task output is available to downstream tasks: `${{ tasks.<id>.outputs.* }}`.

## setVars

Projects action output fields into workflow runtime profile:

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

- Left side = path under `vars`. Right side = JSON path in action output.
- Runner executes `setVars` before reporting task complete. Failure = task failed.
- Can only patch `vars.*`. Never `workflow`, `stage`, `work`, `issue`, `workspace`.
- Recovery tasks can overwrite same `vars.*`.

## Artifacts

Declaration of outputs to capture. Best-effort: skip if missing, never fail task.

```yaml
artifacts:
  files:
    - path: ${{ openspecChangeDir }}/proposal.md
```

## expect

Workflow-owned task completion contract, separate from Action Input. The Runner's Workflow
task executor receives it alongside the rendered input and applies it after Action execution;
actions and runtime modules never interpret it. Only `expect` failure fails the task.

```yaml
expect:
  files:
    - path: ${{ openspecChangeDir }}/proposal.md
  markers:
    - path: ${{ openspecChangeDir }}/review.md
      oneOf:
        - <promise>PASS</promise>
        - <promise>FAIL</promise>
```

Path must exist = put in both `expect` + `artifacts`. Optional = `artifacts` only.

## Error code & recovery

Action output's error fields (`errorCode`, `promise`, etc.) are the action's own contract.

Recovery `when` matches any field: `errorCode=base-moved`, `promise=FAIL`, `errorCode=conflict`.

No global error enum. No engine understanding of specific error meanings.
Recovery design: `recovery.md`.

## OpenCode action

`mohist/opencode` is a runtime-specific Action. Direct Workflow use is Inline Agent
execution; the Action is not a Mohist Agent and does not resolve Agent definitions. Its
runtime is selected by `uses`, so input has no `kind` or `type` discriminator.

```yaml
uses: mohist/opencode
with:
  session: plan
  prompt: ${{ prompts.proposal }}
  options: ${{ vars.agent }}
```

The action receives only the rendered `prompt`, optional logical `session` name, and optional
OpenCode-native `options`. Workflow supplies `expect` separately as the task completion
contract. The action never reads `vars.agent` as a hidden fallback. Output is `null` unless
an expectation matches a promise marker, in which case it is only:

```json
{ "promise": "PASS" }
```

Runtime Session identity, model, usage, transcript, diagnostics, and expectation detail are
stored in their owning models rather than duplicated in action output. Concept ownership:
[`../agent-execution.md`](../agent-execution.md). OpenCode implementation:
[`../opencode-runtime.md`](../opencode-runtime.md).

## GitHub PR actions

`mohist/create-github-pr`, `mark-github-pr-ready`, `push`, `merge-github-pr` are normal workflow actions.

- `create-github-pr`: pushes workflow branch, creates/updates draft PR. Outputs stable PR identity.
- `mark-github-pr-ready`: marks draft PR ready. Idempotent if already ready.
- `push`: syncs local branch to remote PR head. Can use `forceWithLease`.
- `merge-github-pr`: squash-merges PR. Must wait for PR checks first.

PR checks wait = internal precondition of merge action, not a stage-level check. Polls `gh pr view --json statusCheckRollup`. Empty checks → wait with grace window (120s). Failed checks → `errorCode: pr-checks-failed`. No implicit auto-fix — profile must declare explicit recovery.

Full task graph: `builtin-workflows/github-pr.md`.
