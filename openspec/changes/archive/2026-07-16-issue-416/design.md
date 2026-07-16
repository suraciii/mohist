## Context

Project Space owns repository declarations. The current `ProjectGrain` can persist an
empty `RepositoriesJson` list, lets callers rename a repository or change its default
state through a general update, and promotes another repository when the default is
deleted.

The existing persistence shape already stores repository declarations in
`Projects.RepositoriesJson`; `RepositoryInfo` contains the required execution metadata:
`name`, `gitUrl`, `baseBranch`, and `isDefault`. The change therefore strengthens the
Project model and its API rather than adding a repository aggregate, join table, Runner
protocol, or Issue field.

`mo repo` is the only repository-management command group. `mo project create` currently
sends only a name, so it cannot establish the first declaration. The CLI already has an
`ICommandExecutor` seam for Git inspection and shared project resolution, output, and
HTTP-envelope helpers.

Constraints:

- Repository declarations are Project-local resources. Their resource name is the stable
  handle; this change does not consume that handle from Issue state.
- A usable Project must always have exactly one default declaration. The Project aggregate
  is the sole writer of that invariant.
- A local path is bootstrap input for the CLI only. It is never Project or repository
  metadata sent to the server.
- The existing `RepositoriesJson` column remains the persistence boundary. No schema or
  Runner protocol change is required.

## Goals / Non-Goals

**Goals:**

- Make creation and every successful Project mutation preserve one or more complete
  repository declarations with exactly one default.
- Normalize persisted declarations before the server accepts traffic, without guessing
  missing repository metadata.
- Expose a single project-scoped `mo repo` command surface and make `mo project create
  --path` produce the initial declaration from local Git metadata.
- Keep server and CLI failures actionable, deterministic, and free of partial mutation.

**Non-Goals:**

- Do not add, persist, query, or change Issue-specific repository selection.
- Do not prevent deletion based on Issue state, introduce cross-Project repository sharing,
  or coordinate a change across repositories.
- Do not add a Web repository-management screen, a Runner checkout protocol, or a local
  checkout path to Project state.

## Decisions

### D1: Repository declarations remain Project-owned state, with one domain policy for their invariants

`RepositoryInfo` remains the serialized declaration shape:

| Field | Meaning | Rule |
|---|---|---|
| `Name` | Project-local stable resource name | Non-empty; unique with `OrdinalIgnoreCase`; immutable after creation |
| `GitUrl` | Git address used by execution | Non-empty |
| `BaseBranch` | Branch used as the repository base | Non-empty after normalization; omitted or blank input becomes `main` |
| `IsDefault` | Project-wide default marker | Exactly one entry is `true` |

`ProjectInfo.Repositories` remains the ordered collection stored in `RepositoriesJson`.
Introduce one small, pure Project-domain policy for repository declarations. It validates a
complete list, normalizes a legacy list, and applies add, metadata-update, default-select,
and remove operations. `ProjectGrain` invokes that policy before replacing its in-memory
list and persists the resulting list once. The startup upgrade invokes the same validation
and normalization policy, so the default-selection rule cannot diverge between normal
commands and historical data.

The policy does not become a grain, repository store, or public API. `ProjectGrain` remains
the state authority and the only normal mutation entry point.

The supported transitions are deliberately narrow:

| Operation | Result |
|---|---|
| Create Project | Creates one complete declaration and marks it default in the same persistence operation as the Project |
| Add | Rejects a duplicate or incomplete declaration; preserves the current default unless `setDefault` is true |
| Update | Changes only supplied `gitUrl` and/or `baseBranch`; requires at least one field and preserves name/default state |
| Set default | Finds the named declaration before changing the collection, then makes it the sole default; selecting the current default is a no-op |
| Delete | Removes only an existing non-default declaration; a default deletion is a conflict and does not choose a replacement |

Every operation validates before changing the collection. Missing names return not-found;
validation and conflict failures leave the list and default selection unchanged. The Project
query model resolves the flagged default rather than falling back to the first list entry;
the invariant and startup upgrade make a fallback unnecessary and would otherwise hide
corrupt state.

**Alternative considered:** keep validation in routes and let each grain method update the
list independently. Rejected because create, API mutations, and the upgrade would each need
to reproduce ordering, case-insensitive uniqueness, and default logic. A single pure policy
keeps untrusted persisted data and live changes subject to the same rules without adding a
new runtime boundary.

### D2: Project creation accepts an initial declaration and has no repository-less success path

The project-create request changes to contain the Project name and an initial repository
declaration:

```json
{
  "name": "product-a",
  "repository": {
    "name": "product-a",
    "gitUrl": "git@example.com:team/product-a.git",
    "baseBranch": "main"
  }
}
```

`isDefault` is not accepted for the initial declaration. The server derives it as `true`.
`ProjectRoutes` validates the request and passes the complete declaration to a revised
`IProjectGrain.CreateAsync`. The grain writes the Project row only after the initial
declaration is valid and part of the serialized state. A failed validation or persistence
operation therefore cannot expose an empty Project.

The existing Web project-create client must send this complete declaration through its
existing API boundary. This is request-contract alignment only; repository listing and
management UI are not added here.

### D3: Repository HTTP endpoints keep their resource paths and separate metadata updates from default selection

Repository operations remain under the resolved Project resource. Successful mutations
return the updated Project, which gives clients the resulting collection and default state.

| Endpoint | Request body | Semantics |
|---|---|---|
| `GET /api/projects/{projectRef}/repositories` | none | Returns the declaration list in order |
| `POST /api/projects/{projectRef}/repositories` | `{ name, gitUrl, baseBranch?, setDefault? }` | Adds one declaration; `setDefault` selects it atomically |
| `PATCH /api/projects/{projectRef}/repositories/{name}` | `{ gitUrl?, baseBranch? }` | Updates metadata only; at least one field is required |
| `PATCH /api/projects/{projectRef}/repositories/{name}` | `{ setDefault: true }` | Selects the named declaration as the sole default |
| `DELETE /api/projects/{projectRef}/repositories/{name}` | none | Deletes a non-default declaration |

`PATCH` accepts either a metadata update or the explicit default-selection command, never a
mix of both. It does not accept `newName`, `isDefault`, or `setDefault: false`. This keeps
the stable resource name and default transition out of a general patch operation.

Route handlers translate domain outcomes into the existing error envelope:

| Condition | HTTP result | Required diagnostic |
|---|---|---|
| Blank required metadata, absent initial declaration, or empty update | `400` | Names the missing or invalid input |
| Case-insensitive duplicate name | `409` | Identifies the conflicting repository name |
| Attempted default deletion | `409` | Identifies the default and tells the caller to select another default first |
| Unknown Project or repository | `404` | Identifies the unresolved Project or repository |

The API exposes `isDefault` only as the resulting state. It never accepts a client request
to clear a default without choosing another declaration.

### D4: A startup data upgrade establishes the invariant before serving requests

No EF schema migration is necessary because `RepositoriesJson` already persists all four
declaration fields. Add an idempotent Project repository upgrade immediately after
`db.Database.Migrate()` and before either host instance starts accepting requests. Factor the
database initialization sequence used by `Program` and `BuildAlternateApp` so both execute
the same migration and upgrade sequence.

The upgrade reads Projects in deterministic `Id` order, deserializes their declarations, and
uses the domain policy to create a proposed normalized list:

- one existing default remains default;
- with no default, the first declaration becomes default;
- with multiple defaults, the first marked declaration remains default and later marked
  declarations are cleared;
- list order, names, Git URLs, and base branches are preserved exactly.

The upgrade first validates every Project and prepares all changed JSON values. It then writes
the complete set in one database transaction. A malformed JSON document, empty declaration
list with no recoverable metadata, blank required field, or case-insensitive name conflict
aborts initialization with a diagnostic naming the Project and relevant declarations. No
Project row is modified when validation fails. Successful reruns find an already-valid list
and write nothing.

Existing Project and Issue identities remain unchanged. Execution paths that use a Project's
default declaration continue to receive the same Git URL and base branch once a valid
single-repository declaration is marked default. This is continuity of the existing default
resolution path, not a change to Issue repository data or behavior.

**Alternative considered:** silently use the first declaration whenever a read observes no
default. Rejected because it permits invalid persistent state to continue and makes default
selection depend on an incidental order. The startup upgrade makes the repair deterministic;
unrecoverable data stops with an operator-actionable error instead of invented metadata.

### D5: `mo project create --path` resolves Git metadata locally and sends only the declaration

`mo project create <name> --path <path>` requires `--path`. The command uses the existing
`ICommandExecutor` seam and a private bootstrap helper in the project command module to
resolve all initial-repository fields before issuing HTTP:

1. Canonicalize `<path>` and verify it is a Git work tree with a reachable `HEAD` commit.
2. Resolve the work-tree root. Its directory name is the deterministic repository resource
   name.
3. Read `origin` with `git -C <root> remote get-url origin`; an absent or blank value is not
   a usable Git URL.
4. Resolve the base branch from `refs/remotes/origin/HEAD`; if that symbolic reference is
   unavailable, use the checked-out branch only when it is a named branch. A detached HEAD
   or no branch is an actionable failure.
5. Send the D2 create body. The original path, work-tree root, remote alias, and any local
   checkout data are never included in the request.

The command does not fall back to `main` for an unknown Git branch: `main` is the default
only for an explicitly added repository whose optional base-branch argument is omitted. A
failed local inspection produces no HTTP request.

No new process abstraction is introduced. The helper uses the injected executor already
available through `MohistCliApi`; CLI tests provide command results through a fake executor.

### D6: `mo repo` is the single management surface and renders repository state

`RepositoryCommands` owns the complete command group:

```text
mo repo list
mo repo add <name> --git-url <url> [--base-branch <branch>] [--set-default]
mo repo update <name> [--git-url <url>] [--base-branch <branch>]
mo repo set-default <name>
mo repo delete <name>
```

Every subcommand reuses `ProjectRefOption()`, `OutputOption()`, and
`api.ResolveProject(project, projectId)`. Thus `--project` is canonical,
`--project-id` remains its alias, and missing explicit or active scope fails before any HTTP
request. `repository` remains an optional root alias; `delete` is canonical, with existing
`remove` and `rm` aliases retained as equivalent convenience commands.

`add` normalizes an omitted or blank `--base-branch` to `main` in its request and sends
`setDefault` only when requested. `update` requires at least one supported metadata option;
the parser rejects `--new-name` and `--set-default`, so callers cannot bypass the separate
identity and default transitions. `set-default` sends only `{ "setDefault": true }`.

All commands use the shared `Print*WithOutputAsync` helpers. JSON output prints the successful
server data. Table output uses the repository renderer for both a list and a successful
mutation result: when the result is an updated Project, the renderer reads its
`repositories` collection. The table has `name`, `git URL`, `base branch`, and `default`
columns and visibly marks the sole default. It never renders legacy `path` or `remote`
columns, nor the unrelated Project-list empty state.

The CLI does not reconstruct server errors. It preserves the server's readable error envelope
and non-zero exit code, so duplicate, missing-repository, default-delete, and Project
resolution failures retain their actionable diagnostics.

### D7: Coverage follows the product boundary and uses existing fakes

Server Project specs cover creation with a default, default-preserving add, case-insensitive
duplicate rejection, metadata-only update, idempotent selection, default-delete conflict,
and no mutation on validation/not-found failures. API specs verify request shapes, status
codes, returned repository state, and the absence of repository-less creation.

Add a focused data-upgrade spec using a migrated in-memory SQLite template. It seeds historical
`RepositoriesJson` rows directly and verifies valid-default preservation, deterministic
normalization, order/metadata preservation, and full rollback on invalid input. It does not
run a production database or external Git process.

CLI specs use `RecordingHttpHandler`, `FakeFileSystem`, and a configurable fake
`ICommandExecutor`. They cover Git-path bootstrap success and each local-resolution failure;
the two project-scope forms; active-project fallback; all five `mo repo` commands; add's
`main` default; parser rejection of unsupported update options; default-delete error
propagation; and table/JSON rendering. No CLI test uses a real repository, network, process,
or wall-clock wait.

## Risks / Trade-offs

- **Repository-less historical data:** there is no safe source from which to invent a name,
  Git URL, or base branch. The server refuses to start and identifies the Project rather than
  silently creating unusable state. The operator supplies or repairs the declaration and
  restarts.
- **Startup availability:** a transaction-wide preflight makes malformed legacy data block
  startup. This is intentional: serving an invalid Project would reintroduce the missing-
  default state the change eliminates.
- **Creation API is breaking:** all consumers must provide initial metadata. The CLI owns
  local discovery; the Web client must collect/provide metadata through its existing create
  boundary rather than relying on a server-side path.
- **Git branch inference can fail for unusual clones:** an unset `origin/HEAD` falls back only
  to a named checked-out branch. Detached or unborn repositories fail before creation with a
  specific explanation; no branch is guessed.
- **CLI mutation output has a Project-shaped response:** the repository table renderer accepts
  either a list response or the `repositories` member of an updated Project. This is kept in
  the renderer, not duplicated across five command handlers.

## Migration Plan

1. Add the repository domain policy and revise the Project grain interface, creation, query
   default lookup, and route contracts. Preserve the existing `RepositoriesJson` storage
   shape.
2. Add the deterministic startup upgrade and its data specs. Invoke it after EF migration in
   both server startup paths, before `StartAsync`.
3. Update the existing Web creation request to supply initial repository metadata; do not add
   repository-management UI.
4. Add `--path` Git bootstrap to `ProjectCommands`; replace name-only creation specs with
   fake-Git bootstrap specs.
5. Align `RepositoryCommands`, the repository table renderer, and CLI specs with the approved
   command surface and output behavior.
6. Verify with the focused server and CLI test suites, then run the required repository build
   and test commands from `AGENTS.md`.

Rollback is a code revert. The upgrade is idempotent and only adds or normalizes
`isDefault` flags within existing serialized declarations; it does not alter schema, Project
identity, Issue identity, Git URL, base branch, name, or declaration order.

## Open Questions

- None blocking. Repository declarations are deliberately prepared as stable names for later
  work, but this design does not define how any Issue references them.
