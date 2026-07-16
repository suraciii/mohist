# Implementation Tasks

**Scope:** Project-owned repository resources and the `mo repo` / `mo project create --path` CLI surface. Per-Issue repository targeting is **out of scope** for this change.

## T-001 — Server: repository domain policy, grain, and API routes

**What:** Implement the repository declaration lifecycle in the Project aggregate and expose it through the existing HTTP routes.

**Includes:**
- A pure Project-domain policy for `RepositoryInfo` validation and state transitions.
- `ProjectGrain` changes: repository-backed `CreateAsync`, `Add`, `Update` (metadata only), `SetDefault`, `Delete` (default deletion rejected).
- API route/contract changes under `/api/projects/{projectRef}/repositories`.
- Actionable error envelopes for validation, duplicate name, default-delete, and not-found cases.

**Acceptance:**
- Project grain and API specs cover default-invariant preservation, case-insensitive duplicate rejection, metadata-only update, idempotent default selection, and default-deletion conflict.
- Repository-less Project creation is rejected with a validation error.
- The query model returns the repository flagged as default; no silent fallback to list order.

**Depends on:** —

---

## T-002 — Server: startup repository data upgrade

**What:** Add an idempotent startup upgrade that normalizes legacy `RepositoriesJson` before the server accepts traffic.

**Includes:**
- Upgrade invoked after `Migrate()` and before `StartAsync` in both server startup paths.
- Validation and normalization using the same policy as T-001.
- Deterministic default selection: preserve existing single default; otherwise first declaration; if multiple defaults, keep the first marked.
- Transactional write of all changed rows; full abort with Project-level diagnostics on unrecoverable data.

**Acceptance:**
- Upgrade specs pass for single-repo default promotion, missing-default normalization, multiple-default normalization, metadata/order preservation, and unrecoverable-data rollback.
- Existing issues without repository selection continue to start using the upgraded default repository's Git URL and base branch.
- In-flight workflows retain their repository metadata across the upgrade.
- No Project or Issue identity is changed; schema and `RepositoriesJson` shape remain the same.

**Depends on:** T-001

---

## T-003 — CLI: `mo project create --path`

**What:** Bootstrap a Project from a local Git path by resolving the initial repository declaration locally.

**Includes:**
- Require `--path` on `mo project create <name>`.
- Resolve Git work-tree root, resource name from directory name, `origin` URL, and base branch from `origin/HEAD` or the checked-out branch.
- Send only the resolved declaration to the repository-backed creation API.
- Reject missing/invalid paths before any HTTP request.

**Acceptance:**
- Valid Git path creates a Project with one default repository.
- Missing `--path` or unresolvable Git metadata produces an actionable error and exits non-zero.
- The local path is never sent to or persisted by the server.
- CLI project command specs pass.

**Depends on:** T-001

---

## T-004 — CLI: `mo repo` command group

**What:** Implement the complete `mo repo` management surface with project scoping and output rendering.

**Includes:**
- `list`, `add`, `update`, `set-default`, `delete` subcommands.
- Shared project resolution (`--project` / `--project-id` / active project) and `--output table|json`.
- `add` defaults omitted base branch to `main`; `update` rejects `--new-name` and `--set-default` and requires at least one metadata option.
- Repository table renderer that marks the default and never shows legacy path/remote columns or an empty Project list.
- Pass-through of server-side conflict/not-found errors.

**Acceptance:**
- `list` marks the default in table and exposes `isDefault` in JSON.
- `add` with `--set-default` switches the default atomically.
- `update` without supported options exits non-zero.
- `delete` on the default repository exits non-zero and tells the user to `set-default` first.
- CLI repository command specs pass.

**Depends on:** T-001

---

## Dependency order

```
T-001
├── T-002
├── T-003
└── T-004
```

T-001 must land first. T-002 depends on the domain policy and grain invariants. T-003 and T-004 are independent CLI tasks that consume the new server API.
