# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: In `specs/repository-cli-commands/spec.md` (Requirement "Output goes through the shared output option"), the prose read "exposing `-o table|json` (short form `-o`)" — self-referential (it implied `-o` is the short form of `-o`). The shared helper `MohistCliCommands.OutputOption()` registers `new("--output", "-o")` (verified at `packages/cli/Mohist.Cli/MohistCliCommands.cs:61-66`), so the canonical name is `--output` with `-o` as the short form. Reworded to "exposing `--output`/`-o` (accepted values `table`, `json)". The scenarios below (`list -o table` / `list -o json`) were already correct and unchanged.
  Verification: Re-read the edited requirement; the prose now matches the helper signature, the design D4 ("shared `OutputOption()` (default `json`, formats `table, json`)"), and task AC #10 ("render via `OutputOption()` (`-o table|json`)"). No other text in the spec/proposal/design contradicts the canonical `--output`/`-o` naming.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The `add` requirement prose says the body contains "at least `name`, `gitUrl`, `baseBranch`, and `isDefault`", which is looser than design D3 ("Body carries exactly `name`, `gitUrl`, `baseBranch`, `isDefault`") and task AC #3 ("no `path`/`remote`/`resolvedPath` field"). The spec's `add` scenario already pins the four fields precisely, so the three artifacts are reconcilable, but the prose word "at least" could invite ambiguity during implementation.
  SuggestedAction: During implementation, treat the design D3 "exactly" + task AC negative assertion as authoritative; optionally tighten the spec prose from "at least" to enumerate the exact field set if a future spec pass touches this requirement.
  Status: follow-up

## Verification Summary

Cross-checked every source/file reference in `proposal.md`, `design.md`, and `tasks.json` against the current tree:

- Dual track confirmed: `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs` (top-level, `ProjectIdOption()` only, `--default` on add, `remove` primary) and `packages/cli/Mohist.Cli/MohistCliCommands.ProjectRepo.cs` (nested, `--name` option, `--path` fallback, `--set-default`, `set-default` subcommand).
- Registration site `MohistCliCommands.Project.cs:17` → `project.Subcommands.Add(ProjectRepoCommands.Build(api))` confirmed exact.
- Shared helpers referenced by the design all exist and are used by sibling command groups: `ProjectRefOption()` (`MohistCliCommands.cs:54`), `OutputOption()` (`MohistCliCommands.cs:61`), `Print*WithOutputAsync`, `ResolveProject`, `TableShape.RepoList` (`MohistCliApi.cs:778`, rendered at `TableRenderer.cs:57`).
- Docs target confirmed: `docs/cli-reference.md:127-139` already matches the `mo repo --project` target shape; the gap row sits at line 306.
- Test harness confirmed: `CliRepositoryCommandSpecs.cs` uses `CliTestHarness` + `RecordingHttpHandler` (no real network), matching `design/testing.md`.

### Alignment
- All 11 issue acceptance criteria trace to proposal "What Changes" entries, spec requirements, and task T-001 acceptance criteria (single-entry / nested-gone / `set-default` added / positional name / `--set-default` flag / `delete` primary / `ProjectRefOption()` / shared output / tests / docs / server untouched).

### Completeness
- One capability (`repository-cli-commands`) → one spec file → one task (T-001). Edge cases covered: pre-dispatch `--git-url` rejection, dropped `--default` rejection, no-resolvable-project failure, nested-path parser rejection, alias parity for `remove`/`rm`.

### Consistency
- Spec requirements map 1:1 to proposal Capabilities and design Decisions D1-D6. Task `spec` field points to the correct path `specs/repository-cli-commands/spec.md`. Naming (`mo repo`, `--set-default`, `delete` primary) is uniform across all four artifacts.

### Feasibility
- T-001 is a single complete feature slice (rewrite + delete + deregister + docs + tests in one task); title is feature-level ("Unify repo CLI to single `mo repo` entry with flag-based project scope"), not a technical micro-action. No separate "add tests" / "register DI" / "create file" task. All dependencies (`ProjectRefOption`, `OutputOption`, `Print*WithOutputAsync`, `TableShape.RepoList`, `ResolveProject`) already exist in-tree.

### Dependency completeness
- Single task with `dependsOn: []` (it is first). No cycles possible.

<promise>PASS</promise>
