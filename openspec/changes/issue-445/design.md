## Context

Issue 444 introduced Action manifests and authoritative dispatch-time validation, but the executor still spreads effective Variables into `ActionContext`. Several built-ins then treat `context.variables` as a second input channel:

- `delivery-context.ts` resolves and cross-checks repository, base branch, workflow branch, remote, and GitHub repository values.
- `workspace-prepare`, `merge-ready`, and GitHub Actions select workspace or PR values from Variables.
- `openspec-tasks` reads a build prompt fallback, `archive-change` reads and writes retry state, and the OpenCode prompt-loader context exposes all Variables.

The executor legitimately needs effective Variables to render `${{ vars.* }}`, resolve the workspace, evaluate recovery, and apply `setVars`. Those are engine responsibilities. The Action invocation boundary must stop exposing the same bag after rendering. The proposal defines the motivation; the two capability specs define the required single-channel behavior and the bundled-profile regressions.

The primary stakeholders are workflow authors, who need definitions to reveal complete Action inputs, and Runner maintainers, who need manifests to remain the authoritative contract. Existing custom profiles that relied on fallback behavior are intentionally breaking because the project does not preserve compatibility during active development.

## Goals / Non-Goals

**Goals:**

- Make validated `with` the only Action-owned input source and make that invariant difficult to violate in future built-ins.
- Declare the workspace, Git, and GitHub delivery inputs currently obtained implicitly, with missing required values failing as `invalid-input` before side effects.
- Remove issue-backed delivery cross-checks while retaining external credential enforcement.
- Keep `mohist/local` and `mohist/github-pr` behavior unchanged by binding the new contracts explicitly.
- Remove non-delivery Variable reads from OpenCode/OpenSpec Action paths so the invariant applies to every built-in.
- Update catalog descriptions, user documentation, and focused regressions together.

**Non-Goals:**

- Changing `${{ }}` syntax, namespace resolution, Variable merge precedence, or `setVars` semantics.
- Adding server-side repository or branch policy checks.
- Treating repository metadata comparisons as an authorization mechanism; Git and GitHub credentials remain the boundary.
- Completing the broader capability-based Action host redesign or removing existing non-Variable capabilities such as agent runtime, task insertion, and Variable writes.
- Changing stage order, approval gates, recovery budgets, PR lifecycle, or local delivery strategy.

## Decisions

### 1. Project an Action invocation context that contains no Variables

The executor will continue to build effective Variables internally, render `with` and `expect`, validate the rendered Action input, resolve `workDir`, and run engine checks. Before `definition.run`, it will construct a dedicated invocation context that omits `variables` at both the TypeScript type and runtime object levels. `ValidatedActionContext` and built-in handler signatures will use this narrower shape; unsafe casts back to the old Variable-bearing context will be removed.

`workDir` remains host context because the engine has already resolved and constrained it. Dispatch metadata used by existing capabilities, such as workflow/run identity and optional issue identity for an explicitly selected issue-field source, also remains host context.

`rawWith` remains a narrowly defined syntax carrier, not a second semantic input. The executor first validates the rendered top-level composite field against the manifest. A generating Action may then copy only the corresponding raw subtree into a later task so nested `${{ }}` expressions survive until that task's dispatch. It may not render the raw subtree, inspect it for current behavior, or read any value outside the declared composite field. Tests will lock this exception to `openspec-tasks` task propagation and prove that the generated task performs its own normal render-and-validate cycle.

The prompt-loader context will also drop its `variables` member. The current OpenSpec prompt loader reads only its declared loader `with` fields and `workDir`, so this closes a transitive Variable channel without changing prompt output.

Alternative considered: leave `context.variables` available and remove known reads. Rejected because a later built-in could silently reintroduce the same behavior and no type or runtime boundary would detect it.

Alternative considered: complete the full `ActionHost` capability redesign now. Rejected because removing this one data channel is sufficient for issue 445 and avoids coupling the change to unrelated runtime and server-connection ownership work.

### 2. Replace delivery fallback resolution with direct declared inputs

`delivery-context.ts` will be deleted. Delivery handlers will read validated fields directly and retain only pure value parsing where needed, such as converting an explicit Git repository URL to the `gh --repo` selector.

The target manifest contracts are:

| Action | Required explicit inputs | Retained optional/default inputs |
|---|---|---|
| `mohist/workspace-prepare` | `expectedBranch` | none |
| `mohist/rebase`, `mohist/rebase-status` | `baseBranch` | `remote`; existing squash/message inputs for rebase |
| `mohist/merge-ready` | `baseBranch`, `source`, `remote` | none |
| `mohist/push` | `source`, `target`, `remote` | `force`, `forceWithLease` |
| `mohist/create-github-pr` | `repositoryUrl`, `source`, `target` | draft and existing title/body inputs |
| `mohist/mark-github-pr-ready` | `repositoryUrl`, `prNumber` | none |
| `mohist/merge-github-pr` | `repositoryUrl`, `prNumber` | method and existing subject inputs |
| `mohist/github-pr-status` | `repositoryUrl`, `prNumber` | `expect` |

`target` becomes the sole push/PR target name; the `baseBranch` alias is removed where it only duplicated `target`. `merge-github-pr` no longer discovers a PR by source/target because bundled and intended workflows already persist and pass `prNumber`. `remote` is omitted from GitHub Actions because those handlers invoke `gh` with an explicit repository selector and do not use a Git remote.

The existing `titleFrom`, `bodyFrom`, `messageFrom`, and `subjectFrom` selectors remain declared Action inputs. Their issue lookup uses dispatch `projectId`/`issueNumber`, not Variables, and is content retrieval explicitly requested by `with`, not a delivery-target cross-check. Their fallback to Variable-based issue/project identity is removed.

Alternative considered: keep optional inputs and report `invalid-input` inside each handler when all fallback sources are absent. Rejected because marking structurally required fields in manifests lets validation fail before any handler or side effect and keeps the catalog truthful.

Alternative considered: retain the issue-backed cross-check as defense in depth. Rejected because it compares two input channels rather than enforcing an authorization invariant; after the second channel is removed it only makes declared behavior surprising.

### 3. Remove the remaining OpenSpec and OpenCode Variable reads

`mohist/openspec-tasks` will stop synthesizing a build prompt from `variables.prompts.build`. The bundled profiles already place `${{ prompts.build }}` in the declared nested task prompt input. After the rendered `task` object passes manifest validation, `openspec-tasks` copies only that declared object's raw syntax into generated tasks; it does not inspect or render the raw object itself. Each generated task resolves and validates the copied template at its own dispatch.

`mohist/archive-change` will stop reading legacy `_actions.archiveChange.destination` and `openspecArchiveName` Variables. Instead it will persist operation state in a versioned JSON checkpoint under Git-private metadata, resolved through `git rev-parse --git-path mohist/archive-change/<key>.json`. The key is SHA-256 of `workflowRunId + NUL + workspace-relative source path`; the checkpoint records that run ID, source path, and the exact workspace-relative destination selected for this archive operation.

On the first attempt, the Action chooses the unique date/name destination, validates that both paths remain under their expected roots, and atomically writes the checkpoint through a temporary file plus rename before moving the directory. A retry validates the checkpoint and follows its exact destination: source present/destination absent resumes the move, source absent/destination present resumes staging and commit, both present reports `partial-archive`, and neither present reports `missing-source`. A malformed, mismatched, or escaping checkpoint fails before filesystem or Git mutation. The checkpoint is removed only after successful commit or confirmed no-change completion.

This private checkpoint is internal idempotence state, not Action input and not a workflow Variable. It survives Runner process restart in the same workspace and is discarded with that rebuildable workspace. Deployment drains old in-flight runs, so no compatibility reader for the removed Variable state is added. Date generation remains injectable or controlled with `vi.useFakeTimers`; retry tests cover failure before move, failure after move, collision suffixes, process restart, and crossing midnight without using real time.

`mohist/opencode` will resolve structured prompt loaders without a Variable bag. Model options and prompt text continue to arrive explicitly through `with`, including `${{ vars.agent }}` and `${{ prompts.* }}` expansion performed before invocation.

Alternative considered: add optional `buildPrompt` and `archiveName` inputs to reproduce every fallback. Rejected because the built-in profiles already declare the build prompt, while archive naming is internal idempotence state rather than workflow-authored input.

### 4. Update bundled profiles in the same deployment unit

Every `workspace-prepare` task in both profiles will pass `expectedBranch: ${{ workspace.branch }}`. Local `merge-ready`, rebase, and push tasks and recoveries will pass their branch and remote fields explicitly.

The GitHub PR profile will pass `repositoryUrl: ${{ repository.gitUrl }}` to create, ready, merge, and status Actions. PR consumers will continue to pass `prNumber: ${{ vars.github.pr.number }}`. The create-PR profile entry will remove the currently unused `remote` field, and merge will rely on required `prNumber` instead of branch discovery. All push and rebase recovery tasks retain explicit source, target, and remote bindings.

Profile tests will assert parsed `with` maps rather than only task presence. Runner tests will cover manifest-required failures before command invocation, explicit values winning even when test dispatch Variables disagree, and absence of `variables` on the runtime Action context. Existing local and GitHub PR profile flow specs remain the end-to-end regression boundary.

Alternative considered: inject these values automatically in the executor based on Action name. Rejected because that recreates a hidden input channel and couples the engine to built-in business semantics.

## Risks / Trade-offs

- [Existing custom profiles omit newly required inputs] -> Treat this as the declared breaking change; manifest validation returns field-specific `invalid-input`, and documentation lists each required binding.
- [In-flight runs hold old definition snapshots] -> Deploy only after draining or stopping active runs, or rerun affected stages with a current profile after deployment; do not add fallback compatibility.
- [Server and Runner versions disagree during rollout] -> Deploy them as one version because the bundled YAML and Runner manifests must agree; registration catalog changes use the existing wire shape.
- [Archive checkpoint is stale or corrupt] -> Version it, bind it to the WorkflowRun and workspace-relative source in both its hash key and recorded payload, validate source/destination containment before mutation, and fail closed on malformed or mismatched state.
- [Archive retry crosses a date boundary or follows a collision suffix] -> Write the exact selected destination atomically before rename and test retries with fake time before/after midnight and with pre-existing archive names.
- [Removing issue-backed checks permits a profile to target another accessible repository or branch] -> Make the target visible in `with`; credentials and repository permissions remain the security boundary. A central policy requires a separate issue.
- [A hidden Variable read reappears through a helper] -> Remove Variables from the invocation and prompt-loader objects at runtime, then add boundary tests rather than relying only on source searches.

## Migration Plan

1. Migrate OpenCode/OpenSpec readers first: remove prompt-loader Variables, switch archive retry to the private checkpoint, and verify generated-task template propagation.
2. Change workspace/local Git manifests and handlers together with every affected binding in both bundled profiles; verify the local delivery flow.
3. Change GitHub PR manifests and handlers, remove the remaining delivery fallback/cross-check helpers, and update the GitHub PR profile; verify its full delivery and recovery flow.
4. After every concrete reader is gone, remove Variables from the Action invocation type and runtime object, audit catalog/documentation, and run Runner plus Server tests. This is the same dependency order encoded by T-001 through T-004 in `tasks.json`.
5. Before deployment, drain or stop in-flight workflow runs whose snapshots use the old contracts. Deploy Server and Runner from the same build, confirm the Runner registers the stricter catalog, and run both bundled profile regressions.

Rollback requires rolling back Server and Runner together. Runs created from the new profile definitions must not be dispatched to an old Runner because their new fields may be unknown there; stop them before rollback and rerun from a compatible profile snapshot after the previous version is restored.

## Open Questions

None. Input names, the removal of delivery cross-checks, coordinated rollout, and the absence of compatibility fallbacks are resolved by this design.
