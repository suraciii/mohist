# Design: Reasoning Effort as a First-Class Execution Configuration

See [proposal.md](proposal.md) for motivation and the two capability specs
(`agent-reasoning-effort`, `runtime-reasoning-capability`) for requirements.
The generic capability contract and claim-fence protocol this implements are
specified in `design/agent-runtime-reasoning-capability.md` and the
prerequisite change `issue-557-runtime-reasoning-capability` (design-only);
this change is the executable slice. The runtime-readiness witness it builds
on is already live in `DispatchService` polls.

## Context

Mohist's execution configuration today is `(runtime, model, variant)`:

- **Config write** (`packages/server/src/Mohist.Server/Infrastructure/AgentConfigSchema.cs`):
  `AllowedKeys = {model, variant, runtime}`, `IssueAllowedKeys = {model, variant}`.
  There is no `reasoningEffort` key anywhere.
- **Snapshot freezing**: `AgentJobInput` / `RoutedAgentLaunchPlan`
  (`Agent/Grains/IAgentJobGrain.cs`), `AgentExecutionDefinition`
  (`Infrastructure/AgentExecutionSnapshot.cs`), the `WorkDispatch` `with`
  payload (`AgentJobGrain.BuildDispatchAsync`), and the runner's
  session-target/follow-up options (`runner/src/server/session-target.ts`,
  `followup-handler.ts`) all carry model+variant+runtime and are already
  append-only Orleans records — effort slots in as one more field per record.
- **The smuggling**: `runner/src/runtime/host.ts registrationState()` publishes
  Pi's native thinking levels as the catalog's `variants` map, and
  `runner/src/runtime/pi/runtime.ts` applies `options.variant` via
  `session.setThinkingLevel`. OpenCode treats the same field as a true model
  variant. One field, two meanings; switching an Agent's runtime silently
  changes (or drops) the saved value.
- **Catalog wire** (`IRunnerGrain.cs RuntimeCatalogEntry`): only
  `Models` + `Variants`; no completeness flag, no revision. A missing entry
  cannot be distinguished from "runtime says no".
- **Admission**: `DispatchService.AddPendingDispatchesAsync` claims via
  `IRunnerGrain.TryClaimAgentJobAsync(jobId, projectId)` /
  `TryClaimWorkflowAsync(...)` with no capability evidence; the readiness
  witness gates only on runtime readiness, not capability.
- **Readiness** (`AgentReadinessService`): definition matching compares
  instructions/runtime/model/variant/skills; failure classification has no
  effort categories.

Stakeholders: Server (Orleans grains + API), Runner (TypeScript), Web, CLI,
and users whose saved Pi "variants" are really thinking levels.

## Goals / Non-Goals

**Goals:**

- One canonical `reasoningEffort` value (`off|minimal|low|medium|high|xhigh|max`
  or unset), accepted by every write surface (API, CLI, Web, issue-level
  override), independent from `variant`.
- Freeze `(runtime, model, reasoningEffort, variant)` + capability revision in
  every durable execution snapshot; later Agent edits and catalog changes never
  rewrite a frozen snapshot.
- Versioned catalog evidence: per-model `reasoningEfforts`,
  `supportsReasoningEffort`, `complete`, `capabilityRevision`; `variants` keeps
  only true variants.
- Pure server-side resolver with typed dispositions; only `supported` is
  admitted; absent/incomplete evidence leaves work pending; explicit
  rejections are deterministic preflight failures carrying the frozen tuple.
- Claim-time capability fence so the tuple cannot go stale between resolution
  and claim, plus runner-side rejection of stale dispatch snapshots.
- Native translation only inside adapters: Pi maps effort to its private
  thinking level; OpenCode fails explicitly on effort.
- Readiness matching, Web effort control (catalog-driven), CLI flags, and
  execution evidence (applied effort recorded, never synthesized).

**Non-Goals:**

- Per-launch effort override UX and preview (issue 556).
- Native OpenCode effort support (upstream has none).
- Any migration, aliasing, or compatibility layer for Pi thinking-level
  variants — a deliberate product break (re-enter as `reasoningEffort`).
- Write-time validation against a specific runner's catalog (canonical-set
  validation only; catalog agreement is resolved per launch).

## Decisions

### D1. Canonical vocabulary lives in `AgentConfigSchema`; effort is an ordinary string-or-null key

Add `reasoningEffort` to `AllowedKeys` **and** `IssueAllowedKeys` with value
validation against the canonical set (error message names all accepted values,
mirroring the `runtime` message style). Same string-or-null/non-empty rules as
`model`/`variant`. `AgentConfigSchema.Filter` iterates `IssueAllowedKeys`, so
the issue write-side merge paths (`IssueVariableBuilder`,
`IssueWorkflowProfileStorageIntegrity`, `ConfigService`) pick the key up
automatically. `runtime` stays Agent-owned (issue override rejects it, as
today). A `CanonicalReasoningEfforts` set (and a `ValidateReasoningEffort`)
joins `AllowedRuntimes` in the same static class so CLI/Web/API share one list.

*Rationale:* the schema class is already the converged boundary for both
write surfaces; a second vocabulary type would drift.
*Alternative rejected:* validating the effort only at launch — surfaces as a
runtime failure instead of an actionable write error, exactly what the
proposal removes.

### D2. Effort is resolved and frozen like variant — one new append-only field per snapshot record

- `AgentLauncher` gains `ResolveReasoningEffort(config)` beside
  `ResolveModelAndVariant`, and the `AgentExecutionSnapshot` composition
  (line ~829) carries it.
- `AgentExecutionDefinition`: `[property: Id(6)] string? ReasoningEffort = null`.
- `AgentJobInput`: next free Orleans id after `WorkspaceRepositories` (Id 24) →
  `ReasoningEffort` at Id 25.
- `RoutedAgentLaunchPlan`: `ReasoningEffort` at Id 22 (after `WorkflowRunId`).
- `AgentJobGrain.BuildDispatchAsync`: `with["reasoningEffort"]` written exactly
  when non-empty, beside `model`/`variant`; the runner continues to read the
  frozen payload and never re-reads the Agent definition (already the case).
- `WorkflowItemTranslator` `mohist/agent` translation: forward
  `vars.agent.reasoningEffort` into dispatch options exactly like `variant`.
- Runner: `session-target.ts` session-target definition and the
  `followup-handler.ts` follow-up `options` gain `reasoningEffort: definition.reasoningEffort ?? null`;
  `agent-job-executor.ts` reads `payload.reasoningEffort` via
  `readOptionalString` and passes it to both turn executors.

*Rationale:* every record already documents the append-only id convention;
effort is one more frozen member of the same tuple.
*Alternative rejected:* packing `(model, variant, effort)` into the existing
variant string — recreates the smuggling with three members.

### D3. Catalog wire grows append-only capability fields; Pi stops publishing thinking levels as variants

`RuntimeCatalogEntry` (Server) and `RuntimeCatalogEntry` (runner
`core/types.ts`) both gain: `ReasoningEfforts` (per-model map, canonical
values), `SupportsReasoningEffort`, `Complete`, `CapabilityRevision` (Ids 2–5
on the Orleans record; plain optional fields on the TS interface, so old
runners that omit them deserialize fine on the Server). Rules:

- An entry without `Complete`+`CapabilityRevision` is **non-authoritative** —
  the resolver never derives an explicit rejection from it.
- `host.ts registrationState()`: Pi publishes
  `reasoningEfforts: { [provider/model]: [...thinkingLevels] }` (canonical set
  is exactly Pi's native `off…max`), `supportsReasoningEffort: true`,
  `complete: true` (only once the Pi catalog has actually loaded), and an empty
  true-variant map. OpenCode registers `supportsReasoningEffort: false`,
  `complete: true`, no efforts, its variants unchanged.
- `CapabilityRevision` is derived from catalog **content** (e.g. hash of the
  serialized entry) so an identical re-registration (runner restart, reconnect)
  does not invalidate pending expectations; a real capability change produces a
  new revision while old frozen revisions stay identifiable in snapshots.
- Server-side `RunnerRoutes`/`RunnerRegistryGrain` store the entry verbatim in
  `RunnerInfo.RuntimeCatalogs`; the Web settings/runner queries expose
  `reasoningEfforts` through the existing model-metadata plumbing.

*Alternative rejected:* a mode flag on `variants` ("these are efforts") —
keeps one field with two meanings, the original defect.

### D4. One pure server resolver, five typed dispositions

New `Agent/Services/AgentExecutionCapabilityResolver` (pure, no grain writes,
no claims, no execution): input `(runtime, model, reasoningEffort, variant,
catalog snapshot)` → exactly one of `supported | needs-setup | unavailable |
unsupported_execution_configuration | incompatible_execution_configuration`,
plus the compatible runner identity on `supported`. Decision table:

| Evidence | Tuple | Disposition |
|---|---|---|
| Entry missing / `complete=false` / no `capabilityRevision` | any | `needs-setup` (work stays pending, not terminal) |
| Runtime known but not ready for admission | any | `unavailable` (pending) |
| `supportsReasoningEffort=false` + explicit effort | any | `unsupported_execution_configuration` (preflight failure) |
| Complete entry, model / effort / variant explicitly absent | any | `incompatible_execution_configuration` (preflight failure) |
| Complete entry, all present | — | `supported` + runner identity |

Unset effort imposes no requirement (no rejection on an effort-unsupported
runtime). Explicit rejections are **deterministic**: re-evaluating the same
tuple against the same catalog reproduces the disposition, and the frozen tuple
is recorded in the failure evidence.

*Alternative rejected:* treating a missing catalog as incompatible — converts
a transient runner-registration gap into a terminal user error.
*Alternative rejected:* letting each Runner interpret the saved effort —
produces divergent admission decisions and no durable explanation (per the
capability design doc).

### D5. Admission consumes the resolver on the same snapshot; claims become conditional (capability fence)

Per the prerequisite capability design, the claim APIs gain an immutable
capability expectation:

```text
(owner, workId, runtime, model, reasoningEffort, variant,
 capabilityRevision, runtimeGeneration, connectionGeneration)
```

- `AgentJob` adds a read-only pending-dispatch projection + conditional claim;
  the capability revision is persisted with the ledger dispatch snapshot before
  the work becomes claim-visible.
- Workflow exposes a read-only next-work projection with a stable pending work
  id; `DispatchService` translates it, derives the expectation, and performs a
  conditional Workflow claim; `StoreActiveWorkDispatchAsync` remains the
  durable first-writer snapshot.
- `IRunnerGrain.TryClaimAgentJobAsync` / `TryClaimWorkflowAsync` accept the
  expectation and compare it against the grain's current `RunnerInfo` catalog
  **under `_lifecycleGate`** (heartbeat repair can replace the catalog between
  the registry read and the claim). Mismatch → claim refused, work stays
  pending for re-resolution against the new catalog.
- The Runner validates incoming dispatch snapshots against its current
  `capabilityRevision` and rejects stale ones deterministically (never executes
  with silently changed semantics); rejection re-pends the work.
- `DispatchService.AddPendingDispatchesAsync` runs the resolver **before** the
  claim, on the same runner catalog snapshot that produces the dispatch;
  `needs-setup`/`unavailable` skip the candidate without recording failure.

*Rationale:* resolution and claim must not observe two different catalogs;
the fence is the only point where both sides can be compared atomically.
*Alternative rejected:* checking the revision only at dispatch render time —
a catalog update between render and claim-visibility would freeze a snapshot
that is already stale.

### D6. Native translation stays inside adapters; Pi variant→thinking-level code is deleted

- `pi/runtime.ts`: remove `if (options?.variant) session.setThinkingLevel(...)`
  (both sites, lines ~156 and ~292). Add: `if (options?.reasoningEffort)
  session.setThinkingLevel(mapThinkingLevel(options.reasoningEffort))` where
  the map is a private module function (today the canonical names coincide
  with Pi's native levels; the mapping stays private so Pi may diverge without
  a wire change). The applied effort flows into the existing
  resolved-model/session-facts projection (pi `projector.ts`
  `model_change`/resolved-model payload gains an `appliedReasoningEffort`
  field, only when an effort was frozen).
- `opencode/runtime.ts`: explicit `reasoningEffort` in dispatch options fails
  with category `unsupported_execution_configuration` — never appended to the
  model id, written to the variant, or ignored.
- Cross-runtime guard: only the Pi adapter ever sees a thinking-level value;
  no native name appears in any other adapter's model/variant/effort fields
  (covered by the "native values do not cross runtimes" scenario).

*Rationale:* the canonical value is a product concept; native levels are
adapter details owned by the adapter.
*Alternative rejected:* server-side translation to a native value before
dispatch — bakes Pi's vocabulary into the wire contract and breaks the moment
another effort-capable runtime appears.

### D7. No migration for saved thinking-level variants — explicit invalid configuration

Stored `runtime: pi` + `variant: high` is left byte-identical in storage. On
launch, the complete Pi catalog lists no such variant → resolver returns
`incompatible_execution_configuration` → deterministic preflight failure with
the frozen tuple, surfaced as an AgentJob terminal failure and a readiness gap
("update the Agent's execution configuration"). Nothing is migrated,
reinterpreted, or aliased; the user re-enters the value as
`reasoningEffort`.

*Rationale:* any silent conversion (e.g. auto-move pi variants to effort)
would reinterpret stored data based on runtime identity and re-introduce the
ambiguity this change removes; the proposal mandates the break.
*Alternative rejected:* one-shot migration script mapping thinking-level
variants to efforts — must guess intent for models that have both a true
variant and a level, and still loses information.

### D8. Readiness, evidence, Web, CLI

- **Readiness** (`AgentReadinessService`): `MatchesCurrentDefinition` compares
  the resolved effort; `StructuralGaps` adds an `effort-without-model` gap
  ("A reasoning effort is set without a model"); `IsConfigurationFailure`
  classifies `unsupported_execution_configuration` /
  `incompatible_execution_configuration` (normalized `_`→`-` as today) as
  Needs setup with actionable guidance.
- **Evidence**: session model facts record the **applied** effort (from the
  adapter projection) distinct from model/variant; AgentJob terminal result
  carries the frozen effort beside model and variant; absent effort is recorded
  as absent, never synthesized (no default `medium`).
- **Web**: `AgentProfileEditor` gets a separate effort control fed by the
  catalog's per-model `reasoningEfforts` (via the settings/runner model
  metadata queries); `model-variants.ts`/`ModelOptionList` keep serving true
  variants only. No `reasoningEfforts` / `supportsReasoningEffort=false` →
  the control renders nothing and saves no effort for that selection.
  The Web display surfaces show the stored effort beside model with the
  variant still separate: the shared reader `readAgentModelAndVariant`
  (used by the list and detail pages) also returns `reasoningEffort`, the
  Agent list rows (`AgentListPage`) render it beside model/variant, and the
  Agent detail Agent Config card (`AgentDetailPage`) gains a Reasoning-effort
  row with the edit-timing note naming Reasoning Effort among the
  future-jobs-only keys; an absent effort renders nothing on either surface.
- **CLI** (`MohistCliCommands.Agent.cs`): `--reasoning-effort` (validated
  locally against the canonical set before the request) on create/update;
  mutually exclusive `--clear-reasoning-effort` on update (same
  `ValidateClearSetPair` pattern as model/variant; clears only the effort
  key); `mo agent view` renders the stored effort in the table renderer.
- **Docs**: update `design/runtimes/pi.md` (variant no longer maps to a
  thinking level; effort does) and `design/runtimes/opencode.md`
  (effort-unsupported contract); user docs for Agent configuration.

### D9. Testing strategy

- Server unit tests: schema validation (canonical set, issue-surface
  acceptance, independence), resolver decision table (all five dispositions,
  determinism), snapshot freezing (edit/delete Agent → in-flight job keeps
  effort; catalog bump → frozen revision unchanged), readiness gaps/matching,
  `IsConfigurationFailure` classification, conditional-claim fence (matching
  vs changed catalog).
- Runner unit tests: `host.ts` registration (Pi efforts in `reasoningEfforts`,
  variants map empty of thinking levels), pi adapter (effort → thinking level;
  variant never touches it), opencode adapter (explicit effort →
  `unsupported_execution_configuration`), executor payload plumbing,
  session-target/follow-up options, stale-snapshot rejection.
- Web unit tests: effort control (catalog-driven options, no-support
  rendering) and the list-row/detail-card display of a stored effort
  (present, absent, beside a true variant) via the extended shared reader.
- Spec/system tests covering the two specs' scenarios end-to-end (pending on
  absent catalog; explicit rejection recorded with the tuple; evidence).

## Risks / Trade-offs

- [Saved Pi agents with thinking-level variants stop launching] -> Explicit
  preflight failure with frozen tuple + readiness gap naming
  `reasoningEffort`; release notes call the break; the failure message tells
  the user exactly which key to re-enter. No silent drop.
- [Mixed-version fleet during rolling deploy: old runner + new server] -> Old
  runner omits capability fields → catalog entry is non-authoritative →
  resolver returns `needs-setup` → work stays **pending** (safe, no wrong
  execution) until the runner upgrades. New runner + old server: extra
  registration fields are ignored by the old server; but the old server still
  sends Pi variants the new runner no longer honors — deploy server and runner
  from the same release (monorepo) and roll runners first.
- [Rollback re-opens the smuggling gap] -> After rollback, configs saved with
  `reasoningEffort` fail the old `AgentConfigSchema.Validate` (unknown key) —
  those Agents stop resolving until the effort is cleared or the release is
  forward-fixed; document "clear reasoningEffort keys (mo agent update
  --clear-reasoning-effort is unavailable in the old CLI; use the API/Web)" in
  the rollback runbook. Jobs already frozen with an effort are re-executed by
  the old runner without effort (documented rollback caveat; drain pending
  work before rolling back to avoid it).
- [Pending starvation on incomplete catalogs] -> `needs-setup` is never
  terminal, so a runner that never re-registers a complete catalog holds work
  indefinitely; mitigate with the readiness/needs-setup surfacing (gap +
  observability counters on disposition outcomes) so an operator sees why
  nothing is dispatching.
- [capabilityRevision churn invalidating pending expectations] -> Revision is
  content-derived so runner restarts/reconnects with identical catalogs keep
  claims valid; only genuine capability changes re-pend work.
- [Canonical set grows (e.g. a runtime adds `xxl`)] -> New value is an
  additive vocabulary change: old servers reject it at write time (named-value
  error), old runners never see it (frozen snapshots carry only previously
  valid values).
- [Scope creep via the fence] -> The conditional-claim protocol touches both
  owners (`AgentJob`, Workflow, `IRunnerGrain`); it is unavoidable per the
  prerequisite design — landing catalog fields without the fence would create
  stale-at-visibility snapshots — so it is in this change, not deferred.

## Migration Plan

1. Land code in one change (server + runner + web + CLI ship from the same
   monorepo release); spec tests run in CI against the combined gate.
2. Deploy order: Runners first (new registration fields are additive; the old
   server ignores them), then the Server. Existing dispatches/redeliveries
   carry no effort and keep executing; agents with no saved effort behave
   identically (unset imposes no requirement).
3. On cutover, Pi catalogs re-register with `reasoningEfforts` + revision;
   pending work for agents without thinking-level variants dispatches
   normally. Agents with saved thinking-level variants begin failing
   deterministically at preflight with re-entry guidance — expected and
   visible in readiness.
4. Users re-enter efforts via Web/CLI (`mo agent update --reasoning-effort`).
5. Rollback: roll back server, then runners (same release both ways). Before
   rollback, drain or cancel pending AgentJobs frozen with an effort; clear
   saved `reasoningEffort` keys from Agent configs (old write surface rejects
   the key). Stored data is never rewritten by either direction — the
   rollback-safe-by-append-only record convention guarantees old code can read
   (and ignore) the new fields.

## Open Questions

- **capabilityRevision derivation**: content hash (chosen tentatively) vs a
  monotonic counter per runner — confirm content hash is stable across
  serialization order on both sides before freeze.
- **Fence scope for Workflow non-agent tasks**: the expectation applies to all
  conditional claims; do `mohist/pi` / `mohist/opencode` Actions (non-Agent
  workflow tasks with only `model`/`variant` in options) also freeze the tuple
  with unset effort, or are they exempt until they gain an effort option?
- **Web issue-level override UX**: the issue write surface accepts
  `reasoningEffort` (D1), but does the Issue model selector expose an effort
  control in this change, or Agent-profile only (per-launch UX is issue 556)?
- **needs-setup observability shape**: gap code + disposition counters vs a
  dedicated runner-capability status panel — decide during Web implementation.
- **OpenCode upstream effort support**: if upstream adds native effort later,
  only the OpenCode adapter and its catalog registration change; confirm the
  canonical set covers its naming before wiring (fold or map privately).
