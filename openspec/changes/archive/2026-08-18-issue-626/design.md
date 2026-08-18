## Context

Issue 626 affects in-flight Workflow runs created when `core/script` still accepted the per-work `resourceProfile` input. The persisted `TaskRun.WithInput` intentionally retains the original declaration, so direct redelivery can send `resourceProfile` to a current Runner whose `core/script` manifest now declares only required `run` and optional `shell` and `timeout`. The strict Runner validator then rejects the task as having an unknown input. `retrySelf` currently clones the complete `DispatchWorkItem.with` map, so recovery can reproduce the same failure.

The existing boundaries already separate the concerns needed for this change:

- `ActionContractValidator` validates newly parsed Workflow definitions against the current Runner catalog. The catalog and `core/script` manifest must remain unchanged.
- `WorkflowItemTranslator` serializes persisted declarations without rendering or rewriting them. Dispatch snapshots also preserve the raw declaration for redelivery.
- `WorkExecutor` clones, injects engine inputs, renders deferred values, validates Action inputs, and invokes the handler.
- `tryRecovery` builds continuation tasks by cloning the original task declaration and metadata, then decrements `recoveryRemaining` for `retrySelf`.

The stakeholders are operators relying on recovery of existing runs, Server and Runner maintainers responsible for persistence and dispatch correctness, and authors of new Workflow definitions. There is no database migration, public API change, new syntax, or dependency requirement. Historical JSON must remain readable without mutating the stored Workflow state.

## Goals / Non-Goals

**Goals:**

- Allow historical Workflow `core/script` task attempts containing top-level `resourceProfile` to pass current Action validation and execute with current behavior.
- Apply the same behavior to direct redelivery and to `retrySelf` continuations.
- Remove only `resourceProfile` from the effective Action input. Preserve `run`, `shell`, `timeout`, templates, task metadata, completion expectations, artifacts, variables, recovery declarations, and recovery budget.
- Preserve strict rejection of every other unknown input and all invalid supported inputs.
- Keep the current `core/script` manifest and catalog free of `resourceProfile`, so new definitions cannot use it as an execution capability.
- Keep the compatibility rule local, deterministic, and removable without a persistence rewrite.

**Non-Goals:**

- Reintroducing resource limits or per-work containment in the Runner.
- Adding `resourceProfile` to the Action manifest, catalog, Workflow syntax, or public API.
- Relaxing unknown-input validation for other Actions or other fields.
- Rewriting existing `TaskRun` records, dispatch snapshots, or recovery declarations.
- Migrating unrelated retired inputs or creating a general version-negotiation framework.

## Decisions

### 1. Normalize at the Runner execution boundary

Add a small pure compatibility helper adjacent to the Runner input-validation code. It receives the workflow task context and the cloned Action input, and returns a new effective input map. For a Workflow task using `core/script`, it removes only the top-level `resourceProfile` key. For all other Actions, agent-job dispatches, and all other keys, it returns the input unchanged apart from the normal defensive clone.

Use the normalized map in `WorkExecutor` before unresolved-reference detection, deferred rendering, `validateActionInput`, work-directory resolution, cleanup input construction, and handler invocation. Do not mutate `DispatchWorkItem.with`.

This placement handles both dispatch forms with one rule: direct redelivery arrives with the persisted field, and a recovery continuation arrives with the field copied by `tryRecovery`. Since each attempt is normalized immediately before execution, the field cannot reach the current validator or handler.

The compatibility gate should recognize Workflow task dispatches, including legacy envelopes with no explicit owner value, while excluding `agent-job` work. The control-plane definition validator remains the authority that prevents new parsed Workflow definitions from declaring `resourceProfile`; the Runner compatibility path exists for persisted Workflow declarations that have already crossed that boundary.

**Alternative considered:** Strip the field in `WorkflowItemTranslator` or `DispatchService`. This would make the first dispatch clean, but it would move execution compatibility into persistence/transport translation, fail to protect recovery-created continuations unless every path is updated, and make it easier to accidentally rewrite the historical declaration. Keeping the raw declaration and normalizing at execution gives direct replay and recovery identical semantics.

**Alternative considered:** Add `resourceProfile` back to the `core/script` manifest and make the handler ignore it. This would expose a retired input in the current catalog, permit new definitions to use it, and imply that resource containment is still supported. It violates the current Action contract.

### 2. Keep strict validation authoritative after normalization

The helper must run before unresolved-reference checks as well as before schema validation. A retired field containing an old template or an invalid historical value is ignored as a whole; it must not cause an unresolved-variable error or type error. The remaining map is passed through the existing validator unchanged.

Consequently:

- `run`, `shell`, and `timeout` retain their original values or templates and continue through current type and required-field checks.
- An input such as `otherUnknown` remains in the map and produces the existing unknown-input error, even when `resourceProfile` is also present.
- An invalid `run`, `shell`, or `timeout` value remains visible to validation and is rejected using the existing error behavior.
- The handler receives no `resourceProfile` property and cannot use its stored value to influence command, shell, timeout, workspace, or resource behavior.

**Alternative considered:** Make `validateActionInput` globally ignore a named unknown field. This would hide the field for non-Workflow callers and every Action that happened to receive it. The compatibility rule must be contextual, not a general validator exception.

### 3. Preserve raw recovery declarations and metadata

Leave `tryRecovery`'s current cloning behavior intact. A `retrySelf` continuation should copy the original raw `with` declaration, including the retired field, along with the supported inputs and all task metadata. The new continuation receives `recoveryRemaining = remaining - 1` exactly once. The next execution applies the same effective-input normalization, so the copied field is never an effective Action input.

This preserves auditability and the existing no-in-place-rewrite rule while ensuring that recovery cannot strand the run. The normalized map is an execution-local projection, not a replacement for the persisted declaration.

**Alternative considered:** Remove `resourceProfile` from the `AddTaskInput` produced by `tryRecovery`. That would avoid carrying the field forward, but would make recovery mutate the historical declaration shape and introduce a second compatibility rule in recovery construction. It is unnecessary when the execution boundary is authoritative.

### 4. Verify the contract at both control-plane and Runner boundaries

Add focused regression coverage in the existing suites:

- Runner input-boundary tests execute a Workflow `core/script` dispatch with valid supported inputs and `resourceProfile`, then assert successful execution, absence of `resourceProfile` in captured handler input, preservation of the original `work.with`, and unchanged supported values/templates.
- Recovery tests cover a failed historical task with `retrySelf`, assert continuation metadata and exact budget decrement, then execute the continuation and assert the retired field is again absent from effective handler input.
- Validation tests combine `resourceProfile` with an unrelated unknown field and with invalid supported values to prove both continue to fail.
- Server profile-validation tests continue to assert that a new `core/script` definition declaring `resourceProfile` is rejected and that the current catalog does not expose the field.
- Dispatch/translator tests assert persisted declarations remain serialized verbatim for redelivery; no database or snapshot rewrite is expected.

## Risks / Trade-offs

- [A historical field can remain in persisted records and be copied across several retries.] -> The normalization is applied on every Workflow `core/script` execution, and each retry decrements the existing recovery budget exactly as before.
- [A future caller could bypass the Server definition validator and send a new `resourceProfile` declaration.] -> Keep the compatibility gate limited to Workflow task dispatches and retain strict Server definition validation; do not add the field to the catalog. Treat bypassing the control-plane contract as outside this compatibility change.
- [Ignoring the retired field before unresolved-reference checks means malformed old values are not diagnosed.] -> This is intentional: the value has no current semantics and must not block recovery. All current supported fields and all other unknown fields remain fully validated.
- [Older Runner builds still reject the field.] -> Roll out the fixed Runner before relying on recovery of affected runs. Rollback must retain a fixed Runner for those runs; reverting only the Runner binary restores the original failure mode.
- [The compatibility rule may outlive the last affected run.] -> Keep it isolated and document its removal as a later, separately verified cleanup after the retention window for old Workflow runs has passed.

## Migration Plan

No data migration is required. Deploy the Runner change as a backward-compatible execution update. Existing dispatch snapshots and `TaskRun` records can remain byte-for-byte unchanged; new Runner processes normalize the field in memory. The Server catalog and definition validator continue to reject new uses of `resourceProfile`.

Rollout steps:

1. Add the pure normalization and focused Runner and Server regression tests.
2. Deploy the updated Runner to the pool that executes affected Workflow runs.
3. Exercise or monitor a redelivery and a `retrySelf` recovery for an affected run, confirming that the Action receives only the current `core/script` inputs.
4. Continue normal dispatch processing; no replay or backfill job is needed.

Rollback is application-version based: keep the updated Runner available for any run whose persisted declaration may contain `resourceProfile`, and roll back Server changes independently if necessary. Do not delete or rewrite historical input. Once all affected runs have completed and the retention period has been established, removal of the compatibility code can be considered in a separate change with data-sampling and rollback criteria.

## Open Questions

- What retention period or operational signal is sufficient to declare that no persisted run can still contain the retired field before removing this compatibility rule?
- Should stripping be observable through a low-cardinality diagnostic counter or debug log, provided the stored value itself is never logged? This is useful for retirement planning but is not required for correctness.
