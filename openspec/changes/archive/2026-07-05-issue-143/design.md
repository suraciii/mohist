## Context

The `mohist/github-pr` profile already collapsed its four per-artifact `core/artifact-exists` plan checks into one `mohist/openspec-artifacts` check (issue-270, `mohist-github-pr.workflow.yaml:127-132`). The `mohist/local` profile is now the inconsistent outlier — it still dispatches four sequential checks (`proposal-complete`, `specs-complete`, `design-complete`, `tasks-valid`) that each verify a single file under `${{ openspecChangeDir }}` (`mohist-local.workflow.yaml:91-110`).

These four dispatches are redundant with the plan tasks' own `expect.files` declarations, which the engine already validates on task completion. They inflate the issue timeline with near-identical rows and add latency without giving the user a signal the task expects did not already provide.

The runner already ships `openspecArtifactsAction` (`packages/runner/src/actions/openspec.ts:123`), which the github-pr profile uses. It accepts a `required` array of `{ path, kind: "file" | "directory" }` entries, distinguishes file presence from directory presence via `isPresentOfKind` (`openspec.ts:518`), accumulates every missing path, and emits a structured `{ kind: "openspec-artifacts", changeDir, present, missing }` output. The single gap: it currently lists only `proposal.md`, `design.md`, and `tasks.json` — it omits `specs/`, treating the specs directory as optional. Local's existing `specs-complete` gate proves specs was never meant to be optional for local; finishing the consolidation means retiring that optionality so both profiles gate the same artifact set.

Stakeholders: anyone authoring or reading `mohist/local` issue timelines. No API, storage, or task `expect.files` changes.

## Goals / Non-Goals

**Goals:**
- Reduce the `mohist/local` plan stage from six checks to three (`plan-artifacts`, `self-review-passed`, `health`), making both built-in profiles gate plan artifacts the same way.
- Make the `mohist/openspec-artifacts` action cover all four plan artifacts (`proposal.md`, `specs/`, `design.md`, `tasks.json`), so one consolidated check fully replaces the four per-artifact dispatches.
- Keep the failure message actionable: name every missing artifact by path.

**Non-Goals:**
- Removing the plan-stage artifact gate entirely (consolidation, not removal).
- Changing the per-task `expect.files` mechanism.
- Touching the `self-review-passed` quality gate or `health` formatting gate.
- Changing the check, build, or integrate stages.

## Decisions

### D1: Extend the existing `openspecArtifactsAction` instead of adding a new action

The action already implements exactly the shape this change needs: a `required` array of `{ path, kind }`, the `isPresentOfKind` file-vs-directory discriminator, accumulation of all missing paths, and a structured `output`. Adding `specs/` is a single append to the `required` array at `openspec.ts:127-131`:

```ts
{ path: join(changeDir, "specs"), kind: "directory" }
```

No new code path, no new registry entry. The consolidated check reuses the action the github-pr profile already depends on.

**Alternatives considered:**
- *New `mohist/local-plan-artifacts` action.* Rejected: duplicates `openspecArtifactsAction` with a profile-specific name; the whole point of issue-270 was a shared, profile-agnostic artifact gate.
- *Keep `core/artifact-exists` but batch all four paths into one check.* Rejected: `core/artifact-exists` (`registry.ts:101`) takes a single `path` and is untyped (no file/directory distinction). Batching would require extending its contract anyway, and `openspecArtifactsAction` already does this better.

### D2: Make `specs/` required (retire the "specs is optional" behavior)

The action's current three-entry set silently permits a missing `specs/` directory. Local's `specs-complete` check never permitted this, so adopting the consolidated check *without* requiring specs would weaken local. The github-pr profile's plan stage always produces `specs/` (it has a dedicated `specs` task), so tightening github-pr to require it carries no practical regression.

**Alternatives considered:**
- *Keep specs optional and let local lose its specs gate.* Rejected: the acceptance criteria require preserving local's existing gate.
- *Add a profile-specific `requireSpecs: true` input.* Rejected: over-engineering. Both built-in profiles want specs required; no profile wants it optional. The action's required set is a single source of truth.

### D3: Keep `self-review-passed` and `health` as separate checks

`self-review-passed` gates on a `<promise>PASS</promise>` marker in `self-review.md` (quality), and `health` runs `git diff --check` (formatting). These are distinct concerns from artifact presence. Folding them into `plan-artifacts` would violate the single-responsibility contract the spec calls out ("plan-artifacts SHALL verify only presence and kind ... SHALL NOT evaluate self-review promise markers or git diff --check"). They stay as-is.

### D4: Leave `core/artifact-exists` registered

After this change, no built-in profile uses `core/artifact-exists`, but it remains registered in `createDefaultRegistry` (`registry.ts:53`) for custom workflow profiles. Removing it would be a separate, breaking change outside this issue's scope.

## Risks / Trade-offs

- **[Github-pr behavior tightens: now fails if `specs/` is absent]** -> The github-pr plan stage always produces `specs/` via its dedicated `specs` task, so this only bites a profile that deletes the specs task. The `github-pr.md` design doc currently states specs is optional ("specs/ 可选——纯性能/重构 issue 无 spec 变更时合法缺失", `github-pr.md:124-125`) and must be updated to reflect the new required contract.
- **[One missing artifact no longer produces four timeline rows]** -> The consolidated check names every missing path in both the message and the structured `output.missing` array, so the single row stays as actionable as the four it replaces.
- **[Wrong-kind false negatives (e.g. `proposal.md` is a directory)]** -> Intentional and spec-required. `isPresentOfKind` treats a path existing as the wrong kind as missing; the failure message reports the path, so the user can diagnose the misnamed entry.
- **[A change that removes the `specs` task from `mohist/local` would now fail the gate]** -> This is the desired invariant: local treats specs as a required plan artifact.

## Migration Plan

This is a non-breaking change at the workflow-profile-id level (no API, storage, or variable contract changes). Deploy order:

1. **Runner first**: extend `openspecArtifactsAction`'s `required` set to include `specs/`. This tightens the github-pr profile immediately, but github-pr's plan stage always produces `specs/`, so in-flight runs are unaffected.
2. **Server YAML**: replace the four `core/artifact-exists` checks in `mohist-local.workflow.yaml` with the single `plan-artifacts` check. New `mohist/local` runs use the consolidated gate; in-flight runs continue on their dispatched definition.
3. **Tests**: update the runner "specs is optional" case to a required-artifact failure case, add the local-profile single-check spec, and update the `VariableScopeSpecs.cs` fixture.
4. **Docs**: update `design/workflow/builtin-workflows/local.md`, `design/workflow/builtin-workflows/github-pr.md` (the stale "specs optional" comment), and the `docs/workflow-profiles.md` example block.

**Rollback**: revert the workflow YAML (step 2) to restore the four-check shape. The runner change (step 1) is additive and safe to leave deployed; it only matters when a profile dispatches `mohist/openspec-artifacts`, and the github-pr profile already produces `specs/`.

**Verification**: run `npm test -w packages/runner` (openspec spec), `npm test` at root (server specs + `VariableScopeSpecs`), and `npm run typecheck` on both packages.

## Open Questions

None. The artifact set, the required-ness of `specs/`, and the separation of quality/formatting gates are all settled by the proposal and the github-pr precedent.
