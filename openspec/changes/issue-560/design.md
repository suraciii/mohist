# Design: Task-First Agent Create-and-Launch

## Context

Today every Agent execution is definition-first: the caller must first mint an
Agent resource (`POST /api/projects/{projectRef}/agents` rejects a body without
`name` and `instructions`), configure a model — because `AgentReadinessService`
reports `model-missing` as a structural `Needs setup` gap that
`EnsureLaunchableAsync` blocks — and only then state the task through the
definition-first launch route
(`POST /api/projects/{projectRef}/agents/{agentRef}/sessions`, see
`Api/AgentSessionLaunchRoutes.cs`). The launch route already owns the contract
this change must compose with:

- a closed field set (`prompt`, `context`, `attachments`) rejected before any
  state via the raw-JSON presence binder;
- a required `Idempotency-Key`, with identities pre-minted deterministically
  from `(projectId, idempotencyKey)` (`StableToken`) and a resume-first replay
  through `IAgentLauncher.ResumeIdempotentAsync`;
- the canonical pipeline in `AgentLaunchCoordinatorGrain` — fingerprint, plan,
  crash-recoverable `AdvanceAsync`, recorded terminal rejections — shared by
  manual, connection, mention, routed, and spawn launches;
- origin-default workspace binding via `InteractionWorkspaceProvisioner`
  (`X-Mohist-Launch-Origin: cli|web`).

Constraints inherited from the proposal and specs: no breaking API changes; no
third execution path (the composed launch must be indistinguishable from a
definition-first launch of the created Agent); a rejected request must leave no
active Agent to clean up; replay follows the existing launch convergence rules.
Related work: composes with #556 (execution configuration preview) and #557
(reasoning-effort configuration); #555 projections are unaffected.

Stakeholders: Server (Agent definition/session routes, launcher, Readiness,
Project surface), Web UI (composer, Agents empty state, agent entity client),
CLI (`mo agent` group), docs.

## Goals / Non-Goals

**Goals:**

- One accepted request (`POST /api/projects/{projectRef}/agent-tasks`) creates
  a complete Agent definition and starts the first AgentJob/AgentSession
  through the existing `AgentLaunchCoordinatorGrain` pipeline.
- Deterministic derivation of every unspecified definition part (name,
  description, baseline Instructions) plus a materialized execution
  configuration, so a created task-first Agent is immediately launchable and
  never `Needs setup` from missing defaults.
- One Project default execution configuration (Runtime, Model, optional
  Variant) with one precedence rule — caller hint → Agent definition →
  Project default — applied consistently at creation, Readiness evaluation,
  and launch-time resolution.
- No orphaned Agent from any rejection, including crash windows; idempotent
  replay under the caller-visible idempotency-key contract.
- Task-first Web composer (`agent-sessions/new`) and CLI `mo agent start`,
  both reusing the existing response projection, table shape, and navigation
  surfaces.

**Non-Goals:**

- Changing AgentJob/AgentSession semantics, the launch-time definition
  snapshot, entry-point equivalence, or the built-in Agent catalog.
- Removing or deprecating definition-first flows (`mo agent create`, Web
  editor, `mo agent install`); they remain the deliberate-configuration path.
- Per-Agent or global (cross-Project) defaults; the default is Project-scoped
  and singular.
- Editing execution configuration inline in the composer for a *selected
  existing* Agent; the create-new path collects hints, the definition editor
  remains the owner of existing definitions.
- Changing the Slack connection path; it already launches task-first through
  its own defaults.

## Decisions

### D1 — One task-first route composing create + canonical launch

New `Api/AgentTaskRoutes.cs` maps
`POST /api/projects/{projectRef}/agent-tasks` under the existing
`ProjectResolutionEndpointFilter`. The route is an *orchestrator*: it validates
the closed field set, derives the definition, creates/adopts the Agent through
`IAgentGrain`, then delegates to the **unchanged**
`IAgentLauncher.LaunchIdempotentAsync` with the created `AgentInfo`. The
launcher resolves the definition from the created Agent exactly as for a
definition-first launch (including `EnsureLaunchableAsync`, which passes
because the derived definition is complete), so the resulting plan, session
metadata, launch-origin workspace defaults, and read surfaces are
indistinguishable from a definition-first launch of that Agent.

Derivation logic (name, description, Instructions, execution-config
resolution) lives in a new `AgentTaskDefinitionFactory` service so the route
stays a thin composition and the derivation is unit-testable in isolation.

**Alternatives rejected:** (a) Web/CLI composing create-then-launch with two
calls — cannot honor the orphan or replay rules (a lost response between the
calls leaves a half-created Agent, and neither call alone owns the outcome);
(b) a new task-first launch pipeline — the spec forbids a third execution path
and it would fork recovery, observation, and read-surface behavior.

### D2 — Replay-first with a caller-visible fingerprint and deterministic Agent identity

Replay must return the original outcome *including the created Agent's
identity* without re-deriving or re-creating. Two mechanisms make this work
with the existing coordinator and no new state:

1. **The coordinator request fingerprint folds caller-visible inputs only.**
   The task-first route builds the `AgentLaunchCoordinatorRequest` with
   `AgentRef` = the caller's `name` hint (or empty), `Runtime` = the caller's
   runtime hint, plus prompt/attachments/context/origin/targetId — never the
   derived Agent id or name. A replay can therefore reconstruct the identical
   fingerprint before any Agent exists. The *envelope* still carries the real
   `AgentId`/`AgentName` (from the plan, once created), so the response
   projects them; the fingerprint comparison simply ignores them, the same way
   `StartupContext` is deliberately excluded today.

   The `model` and `variant` hints are caller-visible request fields that
   change the outcome (they are materialized into the created definition), so
   they participate in the fingerprint like every other caller-visible field
   — the same convergence discipline the definition-first route applies.
   `AgentLaunchCoordinatorRequest` gains two append-only nullable fields —
   `Model` (Orleans Id 15) and `Variant` (Id 16) — and
   `AgentLaunchCoordinatorCodec.Fingerprint` folds them as a length-prefixed
   hint block with two invariants: (a) an added, changed, or removed
   model/variant hint produces a different fingerprint, so a retry that
   "fixes" a mistyped `--model` under the same key is a 409 conflict — never
   a silent replay of the original outcome with the old model; (b) a request
   carrying no model/variant hint — every definition-first, connection,
   mention, routed, and spawn launch, and a task-first launch resolved purely
   from the Project default — hashes byte-identically to today's canonical
   form, so plans in flight across the deploy resume without false conflicts
   (the coordinator recomputes and compares the fingerprint on resume).
   Narrowing the replay contract instead (excluding model/variant from
   conflict detection) was rejected: it leaves exactly that silent-ignore
   trap for corrected hints.

2. **The Agent id is pre-minted deterministically from the idempotency key** —
   `agent_{StableToken($"{projectId}\n{idempotencyKey}\nagent")}` — mirroring
   the route's existing `preMintedSessionId`/`InputId`/`TurnId` pattern. The
   route resolves-or-creates by that id: if the grain already holds the
   pre-minted id (a prior attempt crashed between create and plan), the route
   *adopts* it (re-derivation is deterministic, so the content matches;
   adoption overwrites with the same values). This closes the crash window
   where a replay would otherwise mint a second Agent or 409 against its own
   orphaned name.

Route flow: read origin header → resolve workspace name (resolve-only, as the
definition-first route does pre-resume) → build coordinator request →
`ResumeIdempotentAsync` (null = no plan → proceed to validation/derivation/
create/launch; non-null or recorded rejection = return original outcome) →
validate → derive → create/adopt → `LaunchIdempotentAsync` → 201. Attachment
binding, context validation, and the attachment-unbind rollback on failure
reuse the definition-first route's helpers and error codes verbatim.

**Alternatives rejected:** (a) keying the coordinator under a separate
task-first key space — unnecessary divergence; the same key space means a key
reused across both routes surfaces as a fingerprint conflict (409), which is
the correct caller-visible behavior; (b) recording task-first operations in a
new idempotency store — new durable state and a second convergence rule for no
benefit; (c) deriving the Agent id non-deterministically and sweeping orphans
lazily — the sweep cannot distinguish a crashed attempt's Agent from a
deliberate one, and leaves a cleanup window the spec forbids.

### D3 — Validation cascade and the orphan rules

Determinable rejections run strictly before `IAgentGrain.CreateAsync`, in
cheapest-first order, each mapping to the spec's error codes:

1. body shape — closed top-level set (`prompt`, `attachments`, `context`,
   `name`, `runtime`, `model`, `variant`) via the same presence-binder
   pattern; undeclared field → 400 `unsupported_field` naming the field;
   missing `Idempotency-Key` → 400 `idempotency_key_required`;
2. hint shape — `runtime` must be `opencode`/`pi`, `model` must contain `/`
   (`AgentConfigSchema` rules) → 400 validation error naming the hint;
3. task usability — neither prompt text nor attachment → 400 `input_required`;
   all attachments rejected with no text → 400 `input_unusable` with
   per-file results (reuses `ValidateAndBindAgentInputAsync` after resume,
   with unbind rollback on failure);
4. context resolution — same helper and same status/error codes as the
   definition-first route (issue/epic/workspace lookups);
5. execution-config resolution — hints, then Project default; if no model
   resolves → 409-style actionable rejection with code
   `execution_config_unresolvable` whose details name both repairs (supply
   `runtime`/`model`/`variant` hints, or configure the Project default);
   a resolved-but-malformed combination cannot occur (malformed hints were
   rejected at step 2, invalid defaults are rejected at configuration time);
6. name — caller-supplied name conflicts (case-insensitive, across active and
   archived, including reserved built-ins via the existing
   `EnsureNameAvailableAsync` semantics) → 409 `AGENT_NAME_CONFLICT`;
   derived names never conflict (D4).

Rejections at steps 1–6 create nothing. State-dependent determinable
rejections (name conflict, execution-config resolution) are re-evaluated on
replay when no plan exists — the same convergence boundary the
definition-first route has today; recorded *plan* rejections are stable (D7).

**Alternatives rejected:** validating lazily inside `LaunchIdempotentAsync` —
the Agent would already exist, converting every rejection into a rollback
instead of a guard, and widening the orphan window.

### D4 — Definition derivation rules

- **Name** (when no `name` hint): derive from the trimmed prompt — first
  sentence, collapsed whitespace, bounded to a name-length cap (≈ 60 chars),
  Unicode-letter preserving (not an ASCII slug). Uniqueness is probed against
  the full name set (active + archived + reserved built-ins, case-insensitive,
  one `ListAsync(all: true)` per derivation) with deterministic ordinal
  disambiguation: `Base`, `Base 2`, `Base 3`, … Attachment-only tasks (no
  prompt text) derive `Task {short-token}` from the deterministic identity in
  D2. If `CreateAsync` still loses a race with a concurrent creation
  (`AgentNameConflictException`), the route re-disambiguates and retries,
  bounded; exhaustion surfaces as 409 `AGENT_NAME_CONFLICT` with guidance —
  never an unhandled failure. A task-first creation never surfaces
  `AGENT_NAME_CONFLICT` for a *derived* name in the non-racing case.
- **Baseline Instructions**: a small fixed template framing the Agent's role,
  with the task prompt embedded verbatim (non-empty by construction; the
  template itself is non-empty so even an attachment-only task yields usable
  Instructions). Deterministic: identical requests yield identical text. They
  are ordinary Instructions — snapshotted per AgentJob, editable later.
- **Description**: `Created from task: {first line of prompt}` (or
  `Created from attachments` when text-less).
- **Execution configuration**: resolved per the precedence rule (D6) and
  **materialized** into the created Agent's `agentConfig`
  (`runtime` always — falling to `opencode` under the existing rule;
  `model` always; `variant` when one resolves). Materialization makes the
  Agent self-describing in lists, immune to later Project-default changes,
  and structurally gap-free. The materialized bundle passes
  `AgentConfigSchema.Validate` before create.

**Alternatives rejected:** (a) leaving `agentConfig` empty and resolving the
default at each launch — the Agent would be neither self-describing nor stable
against default changes, and the list Readiness hydration would depend on the
current default; (b) hashing the prompt into the name — unreadable and
collision-prone; (c) rejecting unresolvable-execution-config tasks with a
half-created `Needs setup` Agent — explicitly forbidden by the spec.

### D5 — Project default execution configuration storage

One nullable JSON column (`DefaultExecutionConfigJson`) on `ProjectRow`, added
by an EF Core migration (nullable → no rewrite, no backfill). Storage shape:
`{ runtime, model, variant? }`.

- **Write surface:** `IProjectGrain.SetDefaultExecutionConfigAsync(...)`
  replaces any prior value (single default per Project); validation reuses
  `AgentConfigSchema` (`runtime ∈ {opencode, pi}`, `model` contains `/`);
  an invalid default is rejected with a validation error and leaves the
  previous default untouched. Exposed via
  `PUT/PATCH /api/projects/{projectRef}/default-execution-config` (closed
  field set, same binder pattern).
- **Read surface:** included in the Project read (`GET /api/projects/{ref}`)
  as `defaultExecutionConfig` (null when unset), so the Web composer and CLI
  can branch without a second endpoint.
- **Consumption:** a scoped, DB-backed
  `ProjectDefaultExecutionConfigReader` (querier-style, one row read per
  scope, cached per request) feeds Readiness and the task-first route. Grains
  never call `IProjectGrain` from the Agent domain for this — the same
  boundary the `WorkspaceRepositories` snapshot established — so list
  hydration (N agents) costs one read, not N grain calls.

**Alternatives rejected:** (a) storing the default in
`ProjectWorkflowProfiles` variables — wrong owner, untyped, and workflow
variable semantics (patch/delete per key) do not match replace-on-set;
(b) a dedicated grain + table — the value is a single small, rarely-written
project attribute; (c) no Project default at all and hints-only — the spec
requires a default so that "a task alone" launches with two questions at most.

### D6 — One precedence rule, one resolution point

A single resolver (`ExecutionConfigResolver.Resolve(hints, definition,
projectDefault)`) implements per-field precedence — caller hint, then Agent
definition, then Project default; runtime falling back to `opencode` when no
source supplies one; an explicitly malformed value is *never* masked (it is
rejected at its entry point: hint validation, `AgentConfigSchema` on
definitions and defaults). It is used at exactly three sites:

1. **Task-first creation** — definition is empty, so effectively hints →
   default; the result is materialized (D4).
2. **Structural Readiness** (`AgentReadinessService.StructuralGaps`) — gains
   the Project default as the second source for `model-missing` /
   `variant-without-model`: when a configured default resolves the model,
   those gaps disappear and the conclusion follows the existing history rules
   (`Ready`/`Unknown`). `runtime-invalid` and `model-reference-malformed`
   are definition errors and remain gaps regardless of any default.
   `MatchesCurrentDefinition` compares the *resolved* tuple (definition →
   default) against the last execution snapshot, so a default change does not
   flip a completed execution to `Unknown`.
3. **Definition-first launch resolution** — `AgentLauncher`'s definition
   resolution folds the default in as the second source, so an Agent Readiness
   reports launchable dispatches with the model Readiness resolved. The
   launch-time snapshot discipline is unchanged: resolution happens once at
   launch; in-flight jobs are unaffected by later default edits.

Applying the rule at Readiness but not at launch (or vice versa) would make
"Ready" agents dispatch with different models than Readiness reported — the
single resolver exists to make that divergence impossible.

**Alternative rejected:** resolving the default into the definition at
*edit* time (auto-filling agent configs) — mutates definitions users did not
touch and breaks "explicit value is never masked" auditability.

### D7 — Crash-safe rollback through the coordinator plan

If the composed launch converges to a terminal rejection *after* the
definition was created, the Agent must leave the active set. The coordinator
plan gains one append-only flag, `DefinitionCreatedByLaunch` (set by the
task-first envelope). `BeginAbortAfterRejectionAsync`'s abort completion
(including the reminder-driven crash-recovery path) archives the created Agent
via `IAgentGrain.ArchiveAsync` when that flag is set, then marks the plan
completed with the rejection recorded. Archive — not delete — because
sessions/jobs and the attribution principal may reference the Agent, the audit
trail is preserved, and derived-name uniqueness already accounts for archived
names. The route's synchronous path observes the same exception and maps it
to the rejection response; the archival itself is owned by the coordinator so
a route-process crash cannot strand an active Agent from a rejected plan.

503 `launch_setup_pending` (still converging) never triggers archival — the
launch may still complete; the caller retries with the same key.

**Alternatives rejected:** (a) route-only archival — the crash window between
coordinator abort completion and route observation would strand the Agent;
(b) deleting the Agent — breaks referential audit and the principal
invariant; (c) leaving the Agent active with `Needs setup` semantics —
explicitly forbidden ("no active Agent remains from the rejected request").

### D8 — Response projection reuse

The 201 response reuses `AgentSessionLaunchResponse` verbatim — it already
projects `agentId`/`agentName`, `jobId`/`sessionId`/`inputId`/`turnId`,
`workspaceId`/`targetId`/`origin`/`status`, attachment results, and the four
canonical URLs, with `sessionUrl` addressing the created AgentSession page.
No new identity vocabulary: Web types, CLI table shape
(`TableShape.AgentSessionLaunch`), and observation/read surfaces consume the
task-first response unchanged.

### D9 — Web composer reorientation

`AgentSessionComposerPage` is reordered task-first:

- prompt + attachments + context refs render first; Agent selection becomes
  an optional control whose neutral state is "New Agent for this task";
- launch with no Agent selected → new `startAgentTask` mutation in
  `entities/agent` posting to `/agent-tasks` with the caller-generated
  idempotency key; launch with a selected Agent → existing
  `useLaunchAgentSession` (definition-first, untouched behavior);
- execution configuration UI is gated on the Project default read (D5) and
  is recommendation-first: with a default configured, the composer presents
  the resolved default as the labeled recommended execution configuration
  for tasks in the Project — no required question, no hints sent — with an
  optional adjust affordance that opens the catalog-backed selection and
  submits the adjusted values as hints; without one, the create-new path
  requires inline Runtime + Model (Variant optional) and submits them as
  hints — it never dead-ends in Agent settings. Inline model selection is
  catalog-backed everywhere it appears: the composer reuses the definition
  editor's `ModelSelect` fed by `useAvailableModelIds`/`useModelVariants`
  (choices from the Project's model catalog for the selected runtime, with
  variants — the entry to the full options), never a free-form model field.
  This covers the issue's model-selection criterion on the task-first path:
  an understandable, purpose-labeled recommendation (the Project default —
  the owner's choice of what tasks in this Project run on) plus a full-
  options entry; the catalog carries no per-purpose model metadata, so a
  purpose-keyed recommendation engine is explicitly out of scope. The inline
  controls apply only to the create-new path;
- success navigates to the returned `sessionUrl` (existing pattern, including
  the attachment-results interstitial); rejection preserves all composed
  state and surfaces actionable feedback — the launch-feedback taxonomy gains
  kinds for `execution_config_unresolvable` (both repairs) and pending
  convergence (retry with the same key), alongside the existing conflict and
  unavailable-server kinds;
- refinement after launch: the launch result and session view link to the
  created Agent (existing agent-detail path), where the definition editor
  remains the owner of name/Instructions/Skills edits — ordinary edits that
  affect only later AgentJobs.

`AgentListPage`'s empty state swaps its primary action to the task-first
composer (route `agent-sessions/new`) with the definition editor demoted to
the secondary action.

**Alternatives rejected:** (a) always showing execution fields — violates the
"two questions at most" product pattern; (b) routing task-first launches
through the existing mutation with a synthesized Agent created client-side —
two-call composition, rejected in D1.

### D10 — CLI `mo agent start`

New subcommand in `MohistCliCommands.Agent.cs`, registered in the `agent`
group, taking **no Agent argument**: `--prompt`/`--prompt-file` (mutually
exclusive, `BodyInputResolver`), `--attach`, `--name`, `--runtime`
(`opencode`|`pi`), `--model` (`provider/model`), `--variant`, the same
context flags as `mo agent launch` (`--issue`, `--epic`, `--repo`,
`--workspace`), `--project`, `--idempotency-key`, and standard output
selection. Flag validation mirrors `mo agent create`'s
`ResolveTypedAgentConfig` (runtime enum, set/clear conflicts as usage
failures). The command: generates and prints the idempotency key before the
request in table mode (same contract as `launch`), sends
`X-Mohist-Launch-Origin: cli` (CLI workspace default), prints via
`TableShape.AgentSessionLaunch` in table mode and the raw Server response in
JSON mode, exits 0 only for accepted launches (including replay), and renders
`execution_config_unresolvable` naming both repairs (`--runtime/--model/
--variant` or configure the Project default) and pointing at
`mo agent model list` as the entry to view the available models, conflicts
by cause, and pending convergence with retry-with-same-key guidance. No local state is created, so
no local cleanup is ever needed.

**Alternatives rejected:** (a) making `mo agent launch`'s Agent argument
optional — overloads a stable definition-first contract and complicates help
output; (b) `mo agent task`/`mo task` naming — the spec fixes `start`, which
also reads correctly as "start working on this".

### D11 — Testing strategy

Server spec tests mirror the launch-route suites
(`AgentSessionLaunch*Specs`): route shape/validation, orphan rules (no Agent
after each rejection class), idempotent replay (identity return; conflict on
a changed prompt, name, context, attachments, or runtime hint, and on an
added, changed, or removed `model`/`variant` hint; pending; recorded
rejection; crash-window adoption), coordinator equivalence
(session metadata/snapshot parity with a definition-first launch of the same
Agent), Readiness matrix (default resolves / default missing / definition
errors unmasked / resolved-tuple history matching), Project default storage
(valid replace, invalid rejected, read surface), plus codec unit tests
pinning the two fingerprint invariants (the hint-block conflict matrix, and
no-hint requests hashing identically to the pre-change canonical form). Unit
tests cover
`AgentTaskDefinitionFactory` (name derivation incl. disambiguation and
reserved names, attachment-only tasks, Instructions/description determinism)
and the precedence resolver. Web tests extend the composer suite
(task-only launch, inline config gating on the default, failure state
preservation, empty-state routing). CLI tests mirror
`CliAgentCommandSpecs` (flag matrix, key printing, table/JSON shapes, exit
codes, retry guidance).

## Risks / Trade-offs

- [Deterministic Agent ids are derived from caller-supplied keys] → two
  different requests under the *same* key mint the same id; mitigated because
  the fingerprint conflict (409) fires first on replay, and first-writer-wins
  on the crash-adoption path — the id space (`agent_{16-hex}`) is
  indistinguishable from `agent_{guid}` externally.
- [Crash between Agent creation and plan creation] → replay adopts the
  pre-minted Agent (D2) instead of duplicating; the only residual window is a
  *never-replayed* crashed attempt, which leaves one active Agent with no
  execution — visible and archivable through the existing Agent list, not
  silently accumulating.
- [State-dependent pre-plan rejections can change outcome on replay] (name
  conflict introduced by another creator; default configured after an
  `execution_config_unresolvable` rejection) → documented boundary: identical
  to the definition-first route today; recorded *plan* outcomes remain stable.
- [Default change flips Readiness of existing gap Agents mid-list] → expected
  behavior change (the proposal calls it out); `MatchesCurrentDefinition` on
  resolved tuples (D6) keeps completed-history conclusions stable.
- [Archive-on-rejection occupies the name from the archived set] → derived
  disambiguation already probes archived names; caller-supplied retries get
  the actionable 409.
- [Readiness hydration gains a per-scope Project read] → one cached read per
  request scope (D5), not per Agent; the existing N+1 job-history reads
  dominate and are unchanged.
- [Composer UX change (optional Agent selection) may surprise existing
  users] → definition-first behavior is preserved whenever an Agent is
  selected; docs updated (`docs/web-ui.md`, `docs/agent-sessions.md`).
- [`AgentRef` in the coordinator fingerprint doubling as the name hint] →
  subtle coupling; confined to `AgentLaunchCoordinatorCodec.Fingerprint`'s
  documented inputs and pinned by replay spec tests.

## Migration Plan

1. **Server first (additive):** EF migration adding the nullable
   `DefaultExecutionConfigJson` column; `IProjectGrain` + Project routes for
   the default; the precedence resolver and Readiness change (existing agents
   with gaps flip to `Ready`/`Unknown` only once a default is configured —
   no default configured means zero behavior change); the `/agent-tasks`
   route; the coordinator plan flag (`DefinitionCreatedByLaunch`,
   append-only Orleans id). Deploy: single rolling deploy; the column and
   route are additive, old clients never call them.
2. **Web:** composer reorientation, empty state, `startAgentTask` client,
   Project-default read; ships behind the same deploy as the Server surface
   it calls (server-first ordering keeps the UI functional during rollout).
3. **CLI:** `mo agent start`; CLI versions older than this change simply
   lack the command until upgraded.
4. **Docs:** `docs/agent-sessions.md` (Launch Entry Points, Configure an
   Agent), `docs/web-ui.md`, `docs/cli-reference.md`,
   `docs/getting-started.md` (task-first as the default first-run path).

**Rollback:** revert Web and CLI releases independently (server routes they
call are additive and unused after revert). Server rollback: the new route
and column stop being exercised; the Readiness precedence change reverts with
the binary (no persisted state encodes it); agents created task-first before
rollback remain ordinary (complete, launchable) Agents — no data repair
needed. The EF migration is forward-additive; leaving the column in place on
rollback is harmless.

## Open Questions

- Exact wording of the baseline Instructions template and the derived
  description (implementation-detailed; must stay deterministic and
  locale-neutral — final copy during implementation review).
- Derived-name formatting details: separator for disambiguation ordinals
  (`Base 2` vs `Base-2`) and the precise sentence-boundary rule for very long
  or punctuation-only prompts.
- Whether the inline composer execution-config controls should additionally
  consume the #556 execution-configuration *preview* endpoint once it rolls
  out (optional enhancement; catalog-backed selection and the labeled
  Project-default recommendation are committed here and do not depend on
  #556).
- API verb/shape for the Project default write surface (`PUT` vs `PATCH` on
  `/default-execution-config`, and whether clearing the default is allowed in
  v1 or replace-only) — settle against `docs/agent-api.md` conventions during
  implementation.
- Whether `mo agent start` should also accept `--output json:field`
  selection subsets like resource commands, or keep raw-JSON-only per the
  spec (raw-only is specified; revisit only if users ask).
