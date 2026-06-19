## Context

Today there is no project-level label vocabulary an agent or user can discover. The only normative label guidance — the `refactor` usage rule — is hardcoded in the mohist skill text; `useLabels()` can only reverse-infer distinct label keys from existing issues. The server has no storage for label metadata.

The server persists project-scoped data via EF Core (`MohistDbContext` + `IDbContextFactory<MohistDbContext>`, with EF Migrations). Write paths that need serialized concurrency use Orleans grains (e.g. `EpicGrain`, which also has an auto-incrementing counter and cross-aggregate link invariants). Read paths use thin Queriery classes over the DbContext. Routes are minimal-API groups under `/api/projects/{projectRef}/...` resolved by `ProjectResolutionEndpointFilter`. JSON columns are supported (e.g. `HasColumnType("JSON")` + `HasConversion`).

The Issue aggregate is untouched by this change: Issue labels remain the `#149` key-value model. The catalog only describes labels; it never enters or constrains an Issue. See `proposal.md` (motivation) and `specs/label-catalog/spec.md`, `specs/http-api/spec.md`, `specs/cli-interface/spec.md` (requirements).

## Goals / Non-Goals

**Goals:**
- Persist a project-scoped, advisory label catalog with `{ key, description, supportedValues?, origin }`.
- Seed system definitions (starting with `refactor`, migrated from the skill text) that are present for every project and immutable.
- Let users add/update/remove their own definitions via API + CLI.
- Expose the catalog through `mo label list` and `mo label add` / `mo label remove`.
- Keep the catalog purely advisory (no Issue-label enforcement, no server-side AI, no agent invocation).

**Non-Goals:**
- Enforce catalog membership on Issue labels; AI classification; skill consuming the catalog (separate issue); label display metadata (colors); Web management UI (API + CLI first).
- Change the Issue aggregate or the existing distinct-keys label endpoint.

## Decisions

### Decision 1: User definitions in an EF table; system seeds are virtual (not persisted)

A single new `LabelDefinitions` table holds **only user-origin** rows: `{ Id, ProjectId, Key, Description, SupportedValuesJson, CreatedAt, UpdatedAt }`, with a unique index on `(ProjectId, Key)`. `supportedValues` is stored as a JSON column (mirrors the codebase's existing JSON-column pattern). There is **no `Origin` column** — origin is derived: a persisted row is `user`; system definitions are provided in code by a `SystemLabelDefinitions` provider and **merged at read time** (system ++ user, keyed/deduped, system keys reserved).

- **Rationale:** System definitions must be immutable, present for every project, and updatable centrally. Making them virtual gives all three for free: they are never in the DB (so they cannot be PATCHed/DELETED), they appear in every project's read without per-project seeding writes, and updating the provider updates every project on next read. Persisted rows carry only the minimal user data the issue requires ("model simplicity").
- **Alternatives considered:**
  - (a) *Full Epic-style aggregate + grain + counter.* Rejected — the catalog has no auto-increment sequence (key is the natural identity), no cross-aggregate invariants, and config-level write contention. Over-engineering.
  - (b) *Seed system rows into the DB per project on first read.* Rejected — introduces writes on read, per-project backfill/migration complexity, and drift; immutability would require extra guards. Virtual seeds are simpler and safer.
  - (c) *JSON config file per project.* Rejected — diverges from the EF-based storage used for all project-scoped data (Epic, Project, Issue), breaking consistency.

### Decision 2: No Orleans grain — a direct DbContext service

Reads and writes go through a `LabelCatalogService` over `IDbContextFactory<MohistDbContext>` (read list + create/update/delete of user rows, plus system-seed merge and immutability checks). No grain, no counter.

- **Rationale:** Label definitions are configuration data edited occasionally by users. There is no per-entity hot contention and no sequence to allocate. The unique `(ProjectId, Key)` index alone guarantees key uniqueness even under racing writes (DB rejects → mapped to 409).
- **Alternative:** a project-keyed grain to serialize writes. Rejected as premature; can be layered on later without changing the table or API if contention ever appears.

### Decision 3: Key-collision and immutability semantics at the service layer

The service knows the set of system keys from the provider, which lets it distinguish the three cases the spec requires:
- **Create** with a key equal to a system key, or an existing user key → **409** (duplicate). Invalid key/empty description/empty supported value → **400**.
- **Patch/Delete** on a system key → **409** (immutable), row untouched.
- **Delete** on a genuinely missing key → **204** (idempotent); on an existing user key → **204** (removed).

### Decision 4: API under the existing label route group, distinct-keys endpoint untouched

Catalog endpoints hang off the existing group: `GET`/`POST /api/projects/{projectRef}/labels/catalog` and `PATCH`/`DELETE /api/projects/{projectRef}/labels/catalog/{key}`, resolved via the existing `ProjectResolutionEndpointFilter`. The current `GET /api/projects/{projectRef}/labels` (distinct keys from issues) stays as-is — it is still needed by `useLabels()` reverse-inference. This coexistence is the "reconciliation" referenced in the proposal; no existing requirement changes, so the http-api delta is pure `ADDED Requirements`.

### Decision 5: CLI augments the `label` command group

`mo label list` switches to print the catalog (system + user, with `key`/`description`/`origin` and `supportedValues` when present) in the existing table/JSON output modes. Add `mo label add <key> --description <text> [--supported-values a,b,c]` and `mo label remove <key>` (`add` creates `origin: user`; both reject system keys). The distinct-keys behavior remains reachable via the API (the only prior consumer, the skill, calls the API directly).

### Decision 6: `refactor` seed text migrated from the skill; skill text left in place for now

The `refactor` seed's `description` is derived from the skill's "Refactor label discipline" section (technical refactoring that reduces complexity without changing observable behavior). The canonical description now lives in the `SystemLabelDefinitions` provider. The skill text is **not** removed in this issue — skill consumption of the catalog is an explicit non-goal/separate issue — so this change only establishes the data source without creating duplication risk yet.

## Risks / Trade-offs

- **[System keys are reserved — users cannot override a system dimension per-project]** -> Mitigation: this is the intended immutability contract (spec requirement). Projects needing custom guidance add a user definition under a different key. Document the reserved system keys.
- **[`mo label list` output changes from distinct keys to catalog]** -> Mitigation: the distinct-keys API endpoint is preserved; only the CLI default view changes. Prior CLI consumers are minimal (the skill uses the API directly). Low breakage risk; if needed, a distinct-keys CLI view can be added later.
- **[Racing duplicate-key creates]** -> Mitigation: the unique `(ProjectId, Key)` DB index rejects the loser; the service maps the constraint violation to 409.
- **[Seed drift across server versions]** -> Mitigation: virtual seeds are defined in code and versioned with the server, so all projects automatically see the latest seed set on read (a benefit, not a risk).
- **[New table/migration]** -> Mitigation: isolated new table; no foreign keys into Issue/Epic, so no coupling to existing aggregates. Rollback is a single migration revert.

## Migration Plan

1. Add `LabelDefinitionRow` entity + configuration in `MohistDbContext.OnModelCreating` (table, `HasKey(Id)`, unique index on `(ProjectId, Key)`, JSON `SupportedValues` column). Add `DbSet<LabelDefinitionRow>`.
2. Add an EF migration creating the `LabelDefinitions` table. **No data migration** — system seeds are virtual; `refactor` appears for all projects immediately on first read once the provider is deployed.
3. Add `SystemLabelDefinitions` provider (with the `refactor` seed), `LabelCatalogService` (read/merge + user CRUD + immutability/collision mapping), and the `/labels/catalog` route group. Wire `LabelCatalogService` into DI (same scope as `EpicQuerier`).
4. Update CLI `MohistCliCommands.Label.cs`: `list` renders the catalog; add `add`/`remove` subcommands hitting the new endpoints.
5. **Deploy:** startup runs the migration; no backfill. Existing issues, workflows, auth, and runtime are unaffected.
6. **Rollback:** revert the migration (drops the table) and remove the routes/commands/provider. Because the feature is new and isolated, rollback loses only newly-created user definitions (acceptable); there is no impact on Issue data. Re-deploying re-creates the table and system seeds reappear via the provider.

## Open Questions

- **System seed set beyond `refactor`:** the issue defers `kind`/`type`/`module`/`context` to the Plan phase. Proposal: ship only `refactor` as a system seed here (adding more later is a code-only change to the provider — no schema/migration). Confirm.
- **`supportedValues` on the `refactor` seed:** likely omitted (refactor is a boolean-ish marker). Confirm.
- **Exact CLI flag names** for `mo label add` (`--description`, `--supported-values`) — confirm against CLI conventions during tasks; behavior is already fixed by the spec.
- **Web UI** for catalog management — explicitly deferred (non-goal); tracked separately.
