## Context

The platform invariant — introduced and enforced by issue 444 — is that a task's `with` payload may carry only inputs the target Action declares in its manifest; anything else is rejected at dispatch time as `invalid-input` *before* the Action body runs (`packages/runner/src/actions/input-validation.ts:41-50`, invoked from `executor.ts:140-146`).

The server builds an ad-hoc rebase task when an operator runs `mo issue rebase <number>`. That task's `with` has always included a `repository` object (`{name, gitUrl, baseBranch}`) copied from the run-owned repository snapshot (`IssueRoutes.Helpers.cs:127-142`, `BuildRebaseTaskWith`). No `mohist/rebase` Action contract — past or present — declares `repository` as an input (`packages/runner/src/actions/built-ins.ts:236-242` declares only `baseBranch`, `remote`, `squash`, `message`, `messageFrom`), and the rebase Action body has never read it. The field was inert under the old pass-through invocation path; under issue 444's strict validation it is a hard pre-dispatch failure, so every rebase task (and every `mo issue retry`) now dies at `Action 'mohist/rebase' received unknown input 'repository'` and the run cannot recover.

The run-owned repository snapshot is also used at the rebase entry point for two unrelated responsibilities that are correct and must be preserved: rejecting a rebase when the run has no repository context (`IssueRoutes.Rebase.cs:37-39`), and defaulting the base branch from the snapshot when the operator omitted one (`IssueRoutes.Rebase.cs:48-50`). Only the mirroring of the snapshot into the Action's `with` is wrong.

## Goals / Non-Goals

**Goals:**
- Make the operator-triggered rebase task's `with` conform to the `mohist/rebase` manifest so the task dispatches and runs git.
- Preserve the run-owned repository context's two existing entry-point responsibilities (missing-context rejection, base-branch defaulting).
- Leave conflict recovery (`recover:resolve-rebase-conflicts`) and every other behavior untouched.

**Non-Goals:**
- Do not change the `mohist/rebase` Action manifest, inputs, or implementation — it already accepts exactly what it uses.
- Do not relax Action input validation — the strict check is the correct contract; the fix is to stop sending the field.
- Do not audit other server-constructed tasks — rebase is the only one verified to send an undeclared input (issue Non-Goal); another surfaces gets its own issue.
- Do not touch profile template variables like `${{ repository.baseBranch }}` — resolved by the variable renderer on a separate path, not Action inputs.

## Decisions

### Decision 1: Remove the `repository` entry from the rebase task's `with`

The manifest is the authority and the caller must conform. `BuildRebaseTaskWith` will emit only `baseBranch` and `remote` — the inputs the server actually populates and the Action actually accepts. `runSnapshot` continues to flow into the helper's *caller*, not into the payload.

- **Alternative considered:** Relax `validateActionInput` to tolerate undeclared inputs. **Rejected** — explicitly a Non-Goal; the strict contract is what surfaces this class of bug and must not be weakened.

### Decision 2: Drop the now-unused `repository` parameter from `BuildRebaseTaskWith`

After Decision 1 the helper reads nothing from the snapshot, so its signature collapses to `BuildRebaseTaskWith(string baseBranch)`. The single call site (`IssueRoutes.Rebase.cs:56`) already resolves `baseBranch` from `runSnapshot` at lines 48-50 before calling the helper, so no defaulting logic moves into the helper.

- **Alternative considered:** Keep the `repository` parameter to minimize the call-site diff. **Rejected** — it would leave a dead parameter whose name implies the snapshot is still mirrored into the payload, violating the project's "model should be as concise as possible" rule and re-seeding the exact confusion that caused the bug. `runSnapshot` stays in scope at the route handler for its two preserved responsibilities; only the helper no longer needs it.

### Decision 3: Preserve `runSnapshot` use at the rebase route entry point, unchanged

`IssueRoutes.Rebase.cs` keeps reading `runSnapshot` for the `missing_repository_context` rejection (lines 37-39) and the base-branch default (lines 48-50). These are spec requirements 2 and 3 and are unrelated to the payload bug; they are left exactly as-is.

### Decision 4: Rewrite the unit test to encode the corrected contract, with a negative assertion

`IssueRebaseRecoveryTests.BuildRebaseTaskWith_UsesResolvedRepositoryContext` currently asserts the `repository` object is present (`IssueRebaseRecoveryTests.cs:9-26`) — it encodes the dead contract. It is rewritten to assert the payload carries only `baseBranch` and `remote`, and gains an explicit negative assertion that no `repository` property exists on the `with` element (guarding against regression). The test is renamed to reflect what it now asserts (e.g. `BuildRebaseTaskWith_CarriesOnlyDeclaredRebaseInputs`); the `WorkflowRepositoryContext` fixture is dropped since the helper no longer takes it. The sibling `ManualRebaseRecovery_ReferencesNamedPromptAndAgent_NeverInlines` test is untouched.

The existing spec tests (`IssueWorkspaceRepositoryResolutionSpecs.cs:119,147`, `ApiContractSpecs.cs:376`) assert only the HTTP response's `data.baseBranch`, never the internal `with.repository` payload, so they continue to pass unchanged and still cover the defaulting + happy-path + duplicate-detection behavior.

## Risks / Trade-offs

- **[Risk: a rebase task already queued/dispatched before deploy still carries the old payload]** -> No in-flight repair is needed or added. Tasks are constructed at request time, so after deploy every *new* rebase request produces a conforming payload. Stuck runs recover by the operator re-triggering `mo issue rebase <number>` (or `mo issue retry`) post-deploy; the previously failed task is not retroactively rewritten.
- **[Risk: another caller of `BuildRebaseTaskWith` breaks when the parameter is removed]** -> Verified single caller (`IssueRoutes.Rebase.cs:56`); the change is compile-checked by `dotnet build` with `TreatWarningsAsErrors`.
- **[Risk: a server-constructed task other than rebase also sends an undeclared input]** -> Out of scope (issue Non-Goal). The issue states rebase is the only one verified; any other surfaces as its own issue and is not masked by this fix.

## Migration Plan

- No schema, persistence, or API-shape change. No data migration, no feature flag.
- Deploy the server. New rebase requests immediately produce conforming `with` payloads.
- For runs stuck on a pre-deploy failed rebase task, the operator runs `mo issue rebase <number>` (or `mo issue retry`) after deploy to queue a fresh, conforming task.
- Rollback: revert the commit. The previous behavior (buggy payload) returns; no data was migrated, so rollback is clean and stateless.

## Open Questions

None. The manifest already defines the target contract, the single caller is known, and the Non-Goals fix the remaining boundaries.
