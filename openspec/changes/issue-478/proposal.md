## Why

Project, Issue, and WorkflowRun each already own a Variables resource (established by the asset-boundary split), but reading or changing them today means using scope-specific, flat-string `config set/clear --var/--stage-var` flags with ad-hoc `<stage>.k` parsing. There is no single key-value language, no typed value input, and no explicit inheritance semantics: agents must guess whether shell text becomes a string or something else, cannot read one scope without bleeding into another, and cannot clearly express "remove this so it re-inherits." A unified `variable list/get/set/unset` command across all three scopes removes that ambiguity and makes Variables a first-class, consistently-addressable resource.

## What Changes

- Add a unified `variable` command group under `project`, `issue`, and `run`, each exposing `list`, `get`, `set`, `unset` with identical semantics: a dotted key path matching `${{ vars.* }}`, `--stage <stage>` to scope reads/writes to that scope's Stage Variables (absent = workflow-wide), and scope-local `list`/`get` that return only that scope's own stored value.
- `set <key> <value>` always stores the positional value as a string with no implicit type coercion; `set <key> --value-json <json>` preserves boolean, number, object, or array types. The two inputs are mutually exclusive and exactly one is required.
- `unset` deletes the current scope's workflow-wide or Stage value so reads re-inherit the parent scope; the persisted Variables document never holds a `null` to mask inheritance.
- Only `run` offers `list`/`get --effective`, a read-only merge of Project → Issue → Run (optionally by `--stage`); other scopes never expose effective reads, because the merge is a WorkflowRun-derived fact, not a fourth writable resource.
- `run variable` reuses the existing Run target-resolution contract (positional Run ID, or `--issue <number>`, exactly one).
- Write boundaries reject non-object Variables roots, invalid JSON, and invalid key paths with actionable domain errors, leaving the original value unchanged.
- Rename the Server variable routes from `.../workflow-profile/variables` to clean `.../variables` resource paths for Project, Issue, and Run (the effective read routes are already clean); the Runner `setVars` patch targets the renamed Run route.
- The new commands follow the established shared CLI contract (single `--project`, `--json` field selection, stdout/stderr split, exit codes); `--json` stays output-only and is never overloaded as a value input.
- **BREAKING**: Project, Issue, and Run variable API paths move from `workflow-profile/variables` to `variables`.
- **BREAKING**: The legacy `project/issue workflow config set|clear` variable flags (`--var`, `--stage-var`, `--vars-file`) are removed in favor of the unified `variable` commands.

## Capabilities

- `variable-commands`: The unified `variable list/get/set/unset` command group across `project`, `issue`, and `run` — shared dotted key path, `--stage`, string-vs-`--value-json` value typing, `unset` inheritance, scope-local reads, the Run-only `--effective` merged read, and Run target resolution, built on the shared CLI contract.
- `variable-resources`: Project, Issue, and Run Variables as resources on clean `/variables` paths — scope-local GET/PUT/PATCH, the Run-only effective read resources, write-boundary rejection of non-object roots, invalid JSON, and invalid key paths, `unset` clearing a scope declaration without persisting `null`, and Effective Variables remaining a read-only Run-derived fact whose later changes affect only not-yet-dispatched attempts while accepted attempts keep their context snapshot.

## Impact

- **CLI** (`packages/cli/Mohist.Cli`): new `variable` command group (partial class) registered under `project`/`issue`/`run`; reuse `ProjectReferenceResolver`, `ResourceDescriptor`/`JsonSelection`, and `ResolveRunTargetAsync`; remove the legacy `--var`/`--stage-var`/`--vars-file` parsing from `MohistCliCommands.ProjectWorkflow.cs` and the Issue workflow-config commands.
- **Server** (`packages/server`): rename variable routes in `ProjectRoutes`, `IssueRoutes.WorkflowProfile`, and `WorkflowRoutes` from `workflow-profile/variables` to `variables`; harden write-boundary validation in `ProjectWorkflowProfileManager`, `IssueWorkflowProfileManager`, and `WorkflowRunProfileManager`; effective read routes and `WorkflowQuerier` effective reads are unchanged.
- **Runner** (`packages/runner`): update the `setVars` patch (`set-vars-apply.ts`) to the renamed Run variable route.
- **Tests** (`packages/cli/tests`, server tests): new specs for the shared key-path / `--stage` / value-type rules across all three scopes, Run `--effective`, target resolution, write-boundary rejection, and no-remote local usage failures; preserve the attempt-snapshot invariant (accepted attempts keep their context, new attempts use the latest Variables).
- **Docs** (`docs/cli-reference.md`, `design/cli.md`, `design/workflow/variables.md`): align examples and close the `workflow-profile` path and string-only-value gaps.
- No database schema or external dependency change.
