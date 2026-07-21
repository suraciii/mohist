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

`workDir` remains host context because the engine has already resolved and constrained it. Dispatch metadata used by existing capabilities, such as workflow/run identity and optional issue identity for an explicitly selected issue-field source, also remains host context. `rawWith` may remain only as the unrendered representation of a declared composite input needed to preserve templates in generated tasks; it is not a Variable source.

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

`mohist/openspec-tasks` will stop synthesizing a build prompt from `variables.prompts.build`. The bundled profiles already place `${{ prompts.build }}` in the declared nested task prompt input, and `rawWith` preserves that template until generated-task dispatch.

`mohist/archive-change` will stop reading legacy `_actions.archiveChange.destination` and `openspecArchiveName` Variables. Retry idempotence will use the existing source/archive filesystem state: before moving, choose the unique date/name destination; after a move, locate the matching archived change when the source is absent and continue Git staging/commit. Variable persistence used only to feed the removed read path will be deleted. Tests will cover retry after move and collision suffixes, including a date-boundary-independent lookup.

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
- [Archive retry chooses the wrong collision destination] -> Match the full archived change basename/suffix convention, fail on ambiguity instead of guessing, and test retries after rename, collision suffixes, and date rollover.
- [Removing issue-backed checks permits a profile to target another accessible repository or branch] -> Make the target visible in `with`; credentials and repository permissions remain the security boundary. A central policy requires a separate issue.
- [A hidden Variable read reappears through a helper] -> Remove Variables from the invocation and prompt-loader objects at runtime, then add boundary tests rather than relying only on source searches.

## Migration Plan

1. Update the invocation-context and prompt-loader types/projection so built-in code no longer receives Variables; adjust focused executor tests first.
2. Change manifests and handlers, delete delivery fallback/cross-check helpers, and replace OpenSpec Variable-dependent behavior with explicit input or local idempotence logic.
3. Update both bundled profile YAML files and their parsed-definition tests in the same commit set.
4. Update Action/workflow documentation and catalog expectations, then run Runner typecheck/tests and Server tests.
5. Before deployment, drain or stop in-flight workflow runs whose snapshots use the old contracts. Deploy Server and Runner from the same build, confirm the Runner registers the stricter catalog, and run both bundled profile regressions.

Rollback requires rolling back Server and Runner together. Runs created from the new profile definitions must not be dispatched to an old Runner because their new fields may be unknown there; stop them before rollback and rerun from a compatible profile snapshot after the previous version is restored.

## Open Questions

None. Input names, the removal of delivery cross-checks, coordinated rollout, and the absence of compatibility fallbacks are resolved by this design.
