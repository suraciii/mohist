## Context

Two CLI command groups drive the same `/api/projects/{id}/repositories` endpoints:

- Top-level `mo repo` (`repository` alias) — `packages/cli/Mohist.Cli/MohistCliCommands.Repository.cs`. Subcommands: `list`, `add`, `update`, `remove`. Uses positional `<name>`, but every subcommand accepts only `--project-id` (`MohistCliCommands.ProjectIdOption()`), and `add` uses the `--default` flag. Output goes through the legacy raw-print helpers (`PrintGetAsync` / `PrintPostAsync` / `PrintPatchAsync` / `PrintDeleteAsync`).
- Nested `mo project repo` (`repo`/`repository`/`repositories` aliases) — `packages/cli/Mohist.Cli/MohistCliCommands.ProjectRepo.cs`, registered at `MohistCliCommands.Project.cs:17`. Subcommands: `list`, `add`, `set-default`, `remove`. Uses `--name` (not positional), `ProjectRefOption()` (both `--project` and `--project-id`), `--set-default`, and the shared `OutputOption()` + `PrintWithOutputAsync` path. Its `add` also accepts a non-spec `--path` fallback and sends a `path` field in the body.

`design/cli.md` mandates (a) one command name per resource concept, (b) project scope expressed via flag (`--project` / `--project-id`, the kubectl `--namespace` pattern), and (c) the spec 词表 (`delete` is the canonical delete verb). The top-level group violates (b) and uses the wrong flag/verb names; the nested group duplicates the resource concept. `docs/cli-reference.md:306` already records this as a known 实装差距 to collapse.

Constraints:
- **Server is untouched.** The endpoints already form one set; this is purely a CLI-surface refactor.
- **No schema migration**, no data backfill.
- **One breaking surface**: the `mo project repo` path disappears, and the `--default` flag is rejected. Both are deliberate (spec-mandated) and called out in the issue's release/changelog note.
- Reuse existing shared helpers — `ProjectRefOption()`, `OutputOption()`, `PrintWithOutputAsync` / `PrintPostWithOutputAsync` / `PrintPatchWithOutputAsync` / `PrintDeleteWithOutputAsync`, `TableShape.RepoList`, `api.ResolveProject(project, projectId)`. No new abstraction is needed.

Stakeholders: CLI users (the command surface), and the docs (`docs/cli-reference.md`). The server, runner, and web are not affected.

## Goals / Non-Goals

**Goals:**
- Collapse the dual track into a single `mo repo` entry whose subcommands are exactly `list` / `add` / `update` / `set-default` / `delete`.
- Make `mo repo` satisfy the project-scope-via-flag convention on **every** subcommand (`--project` + `--project-id` + active-project fallback) — this is the破口 the issue explicitly fixes.
- Unify the parameter shape: positional `<name>` on all mutating verbs, `--set-default` as the "mark default" flag, `delete` as the canonical delete verb (`remove`/`rm` aliases), and strict `--git-url` on `add` (no `--path`).
- Route every subcommand's output through `OutputOption()` + `Print*WithOutputAsync` (`-o table|json`); retire the legacy raw-print calls on this command group.
- Update `docs/cli-reference.md` (remove the now-resolved gap row at line 306; the repo section at lines 127–139 already matches the target shape).
- Cover the new face with CLI specs (no real HTTP, no wall clock — per `design/testing.md`).

**Non-Goals:**
- Do **not** touch `/api/projects/{id}/repositories` server endpoints or request/response schemas.
- Do **not** change remove/delete semantics (still soft operations); only the verb naming flips.
- Do **not** align other command groups' 词表 (label/agent/etc.) — those are separate issues.
- Do **not** flatten `mo project workflow template/profile` in this issue; that nesting is evaluated separately.
- Do **not** add a deprecation shim or hidden alias for `mo project repo` — the spec mandates hard parser rejection.

## Decisions

### D1: Rewrite `RepositoryCommands`, delete `ProjectRepoCommands`, drop the registration

Keep `MohistCliCommands.Repository.cs` as the single implementation (rewritten in place to the unified face), delete `MohistCliCommands.ProjectRepo.cs`, and remove the `ProjectRepoCommands.Build(api)` registration at `MohistCliCommands.Project.cs:17`.

- *Rationale:* The dual track exists literally as two files/classes; merging means one class wins and the other is removed, not "unified at runtime". Keeping the top-level name (`mo repo`) matches the scope-via-flag principle (repo is a project-scoped resource, not a child resource). Once the registration line is gone, System.CommandLine rejects `mo project repo ...` as an unrecognized command for free — satisfying the spec scenario with no extra plumbing.
- *Alternatives considered:*
  - Keep `ProjectRepoCommands.cs` and make `mo repo` a thin alias — rejected: perpetuates two code paths and the spec demands no alias for the nested path.
  - Introduce a deprecation warning for one release — rejected: the spec explicitly requires parser rejection, and the audience is a single developer's local tool.

### D2: Use `ProjectRefOption()` on every subcommand; retire `ProjectIdOption()` here

Replace `MohistCliCommands.ProjectIdOption()` with the `(projectOpt, projectIdOpt) = MohistCliCommands.ProjectRefOption()` tuple on `list`, `add`, `update`, `set-default`, and `delete`. Each action calls `api.ResolveProject(project, projectId)`, which already handles the active-project fallback and emits a clear error + non-zero exit when nothing resolves.

- *Rationale:* This is the core破口 fix and the convention every other project-scoped command (`mo issue`, `mo epic`, `mo agent`) already follows. The nested group already used this helper, so we are porting that wiring up to the top-level.
- *Alternatives:* Keep `ProjectIdOption()` and add `--project` separately — rejected: `ProjectRefOption()` already encapsulates the canonical-vs-alias description string and is the shared factory.

### D3: Unify the mutating-verb shape — positional `<name>`, strict `--git-url`, `--set-default`

- `add <name>`: positional `name` arg; required `--git-url`/`-u` (reject pre-dispatch with a clear error if absent, mirroring the current top-level guard); optional `--base-branch`/`-b`; optional `--set-default`/`-d`. Body carries exactly `name`, `gitUrl`, `baseBranch`, `isDefault` — **no** `path` field (the nested group's `path`/`--path` behavior is dropped; the existing spec asserts the body omits `path`/`remote`/`resolvedPath`).
- `update <name>`: positional `name`; optional `--git-url`/`-u`, `--base-branch`/`-b`, `--new-name`/`-n`, `--set-default`/`-d`; PATCH body carries only the supplied fields (existing behavior, kept).
- `set-default <name>` (migrated from the nested group): PATCH `{setDefault = true}`. Dedicated verb so the `--set-default` *flag* on `add`/`update` and the `set-default` *subcommand* do not collide.
- `delete <name>`: flip the current `remove` primary → `delete` primary, with `remove` and `rm` as aliases (System.CommandLine `Aliases.Add`). Identical DELETE request.

The `--default` flag is **not** registered; System.CommandLine rejects unknown options with a non-zero exit, satisfying the "dropped `--default` flag is rejected" scenario without bespoke code.

- *Rationale:* Positional resource identifiers match `mo project`/`mo issue`/`mo epic`/`mo agent`. `--set-default` (verb form) reads as an action; `--default` (adjective) is ambiguous. Strict `--git-url` aligns `add` with the spec body contract and the existing assertion that the body has no `path`.
- *Alternatives considered:*
  - Keep `--path` as a hidden alias for `--git-url` — rejected: spec mandates `--git-url`, and carrying `path` in the body would break the existing `RepositoryAdd_WithGitUrl_*` assertion and the spec body shape.
  - Make `set-default` a `--set-default-only` flag on `update` — rejected: the spec enumerates `set-default` as a first-class subcommand.

### D4: Route every subcommand through `OutputOption()` + `Print*WithOutputAsync`

- `list` → `PrintWithOutputAsync(path, mode, nameof(TableShape.RepoList))` (table shape already exists in the enum and already works for the nested `list`).
- `add` → `PrintPostWithOutputAsync(path, body, mode)`.
- `update` / `set-default` → `PrintPatchWithOutputAsync(path, body, mode)`.
- `delete` → `PrintDeleteWithOutputAsync(path, mode)`.

All subcommands gain the shared `OutputOption()` (default `json`, formats `table, json`), resolved via `api.ResolveOutputMode(output)` before dispatch (the existing pattern in the nested `list` and in sibling commands).

- *Rationale:* The shared output path is the established convention; the top-level group's legacy `Print*Async` calls are the outlier. This also gives `-o table` repo rendering for free, since `TableShape.RepoList` is already implemented.
- *Alternatives:* Keep raw prints for mutating verbs and only use `OutputOption()` on `list` — rejected: the spec says **every** subcommand renders through `OutputOption()`.

### D5: Keep a private `ProjectRepositoriesPath(projectId)` helper in `RepositoryCommands`

Both old files had effectively the same path builder. Retain one private helper inside `RepositoryCommands` that produces `/api/projects/{escape(projectId)}/repositories` and throws / surfaces the no-active-project message when the id is null. (`api.ResolveProject` already returns a non-zero exit in that case, so the helper is only reached with a resolved id.)

### D6: Tests — rewrite `CliRepositoryCommandSpecs.cs` to the unified face

The existing three specs all assume the old face (one passes `--default`). Rewrite/extend to cover, at minimum:
- `mo repo --help` lists exactly `list`/`add`/`update`/`set-default`/`delete`.
- `mo repo add <name> --git-url ... --base-branch ... --set-default --project <p>` posts the spec body (no `path`).
- `mo repo add` without `--git-url` is rejected pre-dispatch (clear `--git-url` error, empty request log).
- `mo repo add ... --default` is rejected (non-zero exit).
- `mo repo update <name> --new-name ... --git-url ... --base-branch ... --project <p>` patches the spec body.
- `mo repo set-default <name> --project <p>` patches `{setDefault = true}`.
- `mo repo delete <name> --project <p>` sends DELETE; `remove`/`rm` alias the same request.
- `--project` and `--project-id` both resolve the scope (one canonical, one alias).
- A subcommand with no resolvable project fails clearly without dispatching.
- `mo project repo list` (and any `mo project repo <sub>`) is rejected as unrecognized (non-zero, no request).
- `mo repo list -o table` renders the repo list table; `-o json` prints the raw payload.

All via `CliTestHarness` + `RecordingHttpHandler` (no real network, no wall clock) — matching the existing specs and `design/testing.md`.

## Risks / Trade-offs

- **[Breaking] `mo project repo` invocations stop working.** -> Mitigation: deliberate per spec; release/changelog note tells users to migrate to `mo repo --project`. No deprecation shim by design (spec mandates parser rejection).
- **[Breaking] `--default` flag is rejected.** -> Mitigation: spec-mandated; flag is now `--set-default`. Documented in `docs/cli-reference.md` (already shows `--set-default`).
- **[Behavior change] Nested `add`'s `--path` fallback disappears.** -> Mitigation: any user relying on `mo project repo add --name x --path <p>` migrates to `mo repo add <name> --git-url <url>`. The unified body intentionally drops the `path` field; the existing spec already asserts its absence.
- **[Coverage gap]** If the rewrite misses a subcommand, the parser silently drops it. -> Mitigation: the `mo repo --help` spec scenario asserts the exact subcommand set; any drift fails the spec.
- **[Docs drift]** Stale references to `mo project repo` outside `cli-reference.md`. -> Mitigation: a repo-wide scan during implementation (only `MohistCliCommands.Project.cs:17` and the deleted file reference the class; no other docs use the path).

## Migration Plan

This is a CLI-only change; there is no server coordination and no schema migration.

1. Rewrite `MohistCliCommands.Repository.cs` to the unified face (D1–D5).
2. Delete `MohistCliCommands.ProjectRepo.cs`; remove the registration at `MohistCliCommands.Project.cs:17`.
3. Update `docs/cli-reference.md`: delete the resolved gap row at line 306 (the repo section at lines 127–139 already describes the target shape — leave it).
4. Rewrite `CliRepositoryCommandSpecs.cs` to the unified face (D6).
5. Verify: `dotnet build Mohist.sln` (TreatWarningsAsErrors acts as lint), then `npm test` (server) and the CLI test project.

Rollback: revert the CLI changeset — no server-side state to unwind. The removed `mo project repo` path returns as soon as the old file/registration are restored.

## Open Questions

- None blocking. The spec fully determines the command face, body shapes, and the hard-removal stance. If implementation surfaces a `RepoList` table-shape rendering bug, it is a follow-up on the table renderer, not a design question for this issue.
