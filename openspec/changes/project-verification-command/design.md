# Design: Project verification command

## Ownership and lifecycle

`ProjectRow.VerificationCommand` is nullable only to support existing rows during migration. `ProjectInfo.VerificationCommand` exposes the dedicated value. The Project grain validates and replaces the value while updating the Project timestamp. New Project creation requires a command in the API and CLI/Web creation flows.

The API is `PUT /api/projects/{projectRef}/verification-command` with `{ "command": "..." }`. The body is closed and command text is not normalized beyond validation. There is no clear operation. Generic Project Variables remain unchanged and are not consulted by built-in verification.

## Run binding

The profile coordinator resolves the Project command at the same linearization point as the effective Profile Definition. `BoundWorkflowStart.VerificationCommand` uses a fresh serializer ID. The Run binding participant persists `WorkflowRun.VerificationCommand` and compares it during replay/idempotency. Existing `BoundWorkflowDefinitionJson` remains authoritative for already-bound Runs.

Missing command fails with stable actionable error `project-verification-config-missing` before Runner claim/dispatch. Issue start preflights the Project before committing work-start state; binding also validates for non-Issue and stale callers. Existing active runs without a bound definition are drained/stopped operationally before deployment and are not rebound to current Profile or Project state. No compatibility execution path is retained.

## Built-in task

Both built-in Profiles contain one ordinary `verify` task:

```yaml
- id: verify
  title: Verify
  uses: core/script
  with:
    run: ${{ workflow.verification.command }}
    working-directory: REPOS/${{ repository.name }}
    timeout: 900000
  recovery:
    budget: 2
    handlers:
      - when: error.code=script-failed
        tasks: [recover:fix-ci]
        retrySelf: true
      - when: error.code=timeout
        tasks: [recover:fix-ci]
        retrySelf: true
```

The existing Profile-owned Builder handler and prompt are retained. A Project command is shell script text executed by `core/script` in the repository root; it is visible configuration, not a secret. Custom Profiles may model multiple verification boundaries.

## Dispatch and recovery

The translator emits the immutable Run snapshot as `workflow.verification.command`. Runner renders the ordinary task and reports facts. Server recovery handling attributes any error-bearing follow-up chain to its source attempt, fences duplicate chains, accepts helper definitions from the report, and reconstructs a same-definition `retrySelf` from the persisted source Task. Runner-provided self-retry declarations are scheduling hints only.

Remove lane catalog/classifier/gate/outcome metadata and lane status projection. Existing bound six-task definitions remain unchanged and execute as ordinary tasks. Historical persisted attempt `lane` properties are ignored by the current model; startup does not rewrite WorkflowRun state.

## Persistence

Add a retained-tail nullable `VerificationCommand` Project column after the current latest migration. Update EF model snapshot and raw SQL test schemas. Do not modify the published squashed baseline migration.
