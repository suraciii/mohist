## Context

Mohist's runtime already models issue-level start prerequisites end-to-end:
`Issue.AddPrerequisite` enforces self-reference and idempotent dedupe at the
domain layer (`packages/server/src/Mohist.Server/Issue/Domain/Issue.Prerequisites.cs`);
`IssueGrain.AddPrerequisiteAsync` layers project-scoped existence validation on
top via `LoadIssueSummaryAsync` (`IssueGrain.cs:529-541`, `:653-667`);
`GetStartReadinessAsync` produces the `canStart` / `blocker` read model; and
`StartWorkAsync` blocks start when any prerequisite is undelivered. The read
side already exposes `prerequisiteNumbers`, `prerequisites`, `canStart`, and
`blocker` on every issue read model.

The gap is purely in the **create/edit UX and the create API contract**:

- `CreateIssueRequest` (`IssueRoutes.Dtos.cs:9-23`) carries title/body/labels/
  priority/model/agentConfig/stageModels/workflowProfileId/repositoryName/risk/
  isDraft/attachmentIds — but **no prerequisites**. The create handler
  (`IssueRoutes.Crud.cs:37-116`) allocates a number from `IIssueCounterGrain`,
  calls `IssueGrain.CreateAsync`, applies model metadata, and returns the read
  model via `IssueQuerier`.
- The New Issue dialog (`CreateIssueDialog.tsx`) mirrors exactly those fields
  and sends them through `createIssue` (`entities/issue/api/client.ts:21-27`).
- The backlog editor (`IssueConfigurationCard.tsx:30-39`) parses a raw
  `parseInt` from a numeric `<Input>` and calls `addPrerequisite` — no search,
  no candidate context, no picker.

So the user must already know the exact issue number to model a dependency, at
precisely the moment (decomposition) when that knowledge is least available.

### Constraints / stakeholders

- **No domain-layer change expected** for the invariants — `Issue.AddPrerequisite`
  and `IssueGrain.AddPrerequisiteAsync` already implement them. The spec
  (`issue-create-prerequisites`) explicitly says reuse, don't duplicate.
- **No runner/CLI contract change** (proposal Impact section).
- **All-or-nothing create**: a failed prerequisite validation must leave no
  persisted readable issue (`issue-create-prerequisites` spec, "Validation
  failure leaves no partially configured issue").
- **Existing single-add/remove HTTP contract is frozen** (`POST /issues/{n}/prerequisites`,
  `DELETE /issues/{n}/prerequisites/{pn}`) — the backlog editor keeps using it
  behind the new picker.
- The issue counter (`IssueCounterGrain.NextAsync`) eagerly writes
  `Next = current + 1` before creation proceeds; today, attachment/profile
  validation failures already "burn" the allocated slot. The spec acknowledges
  this as established counter semantics — we do not need to undo the counter.

## Goals / Non-Goals

**Goals:**

- **G1 — Atomic create-with-prerequisites API.** `POST /projects/{projectRef}/issues`
  accepts an optional `prerequisiteNumbers: int[]` and applies it atomically
  with creation, reusing the existing existence/self-reference/dedupe
  invariants, so no partially configured issue is left on validation failure.
- **G2 — Populated read models in the create response.** The `201` body
  returns the full issue read model with `prerequisiteNumbers`,
  `prerequisites`, `canStart`, and `blocker` already reflecting the just-applied
  prerequisites — no second round-trip.
- **G3 — One reusable, searchable, project-scoped issue picker** consumed by
  both the New Issue dialog and the backlog prerequisite editor, replacing the
  numeric-only `Issue #` input.
- **G4 — Selections as removable chips** in both surfaces; the create dialog
  buffers pending selections client-side, the backlog editor drives the existing
  add/remove endpoints behind the picker.
- **G5 — Reuse the start-readiness model** to explain incomplete prerequisites
  and Start eligibility, with no new readiness model introduced.

**Non-Goals:**

- Task-level `dependsOn` inside `tasks.json` (issue-level only).
- Automatic dependency inference from issue text.
- Changes to start-blocking semantics or to the single-add/remove HTTP contract.
- Deep graph-walk circular-dependency detection beyond self-reference (the
  existing domain invariant is self-reference only; see Open Questions).
- Project identity/name migration (#35).
- Picker virtualization/pagination for arbitrarily large issue counts (the
  project issue list is already fetched for the board; see Risks).

## Decisions

### D1 — Extend `IssueGrain.CreateAsync` to accept prerequisites, validated and applied in one persistence step

**Decision.** Add an optional `int[]? prerequisiteNumbers` parameter to
`IIssueGrain.CreateAsync` / `IssueGrain.CreateAsync`. Inside the grain, **after**
the `Issue.Create(...)` aggregate is built but **before** `SaveIssueAsync()`:

1. Resolve the newly allocated `Number` (already in hand from the counter) and
   reject any entry equal to it via the existing `Issue.AddPrerequisite`
   self-reference guard.
2. For each unique prerequisite number, call the **same**
   `LoadIssueSummaryAsync(number)` helper that `AddPrerequisiteAsync` uses
   (`IssueGrain.cs:653`). If any returns null, the create fails fast with a
   clear `PrerequisiteNotFound(number)` error — no persistence happens.
3. Apply `Issue.AddPrerequisite(number)` for each survivor (domain dedupes
   idempotently — `Issue.Prerequisites.cs:11`), then `SaveIssueAsync()` once.

The route handler (`IssueRoutes.Crud.cs:37-116`) maps the new
`CreateIssueRequest.PrerequisiteNumbers` field onto the grain call. On the
grain throwing a typed prerequisite-validation exception, the handler returns
`ApiResults.BadRequest(...)` **without** ever reaching `SaveIssueAsync` —
satisfying the no-partial-issue contract at the source rather than via
compensation.

**Rationale.** The spec mandates that create-time and edit-time existence
semantics "cannot diverge" and that the create path reuse the same
project-scoped check. Performing the validation **inside the grain**, with the
identical `LoadIssueSummaryAsync` used by `AddPrerequisiteAsync`, is the only
design that makes divergence structurally impossible — both paths call the same
private helper on the same grain. Doing it before the single `SaveIssueAsync`
makes atomicity a property of the code path rather than a compensation
after the fact.

**Alternatives considered.**

- *A1 — Keep `CreateAsync` unchanged; pre-validate in the route handler using
  `IssueQuerier`, then call `CreateAsync`, then loop `AddPrerequisiteAsync`.*
  Reuses the existing single-add grain call but (a) duplicates the
  existence-check logic between route and grain, inviting divergence; (b)
  leaves a race window where a prereq is deleted between pre-validation and
  `AddPrerequisiteAsync`, producing a created-but-missing-prereq issue; (c)
  requires the route handler to compensate (delete the just-created issue) on
  partial failure. Rejected: the compensation path is exactly what the spec
  wants to avoid.
- *A2 — Route handler loops `AddPrerequisiteAsync` after `CreateAsync` with no
  pre-validation.* Violates "no partially configured issue on failure" — the
  issue is already persisted after `CreateAsync`. Rejected.
- *A3 (chosen) — Extend `CreateAsync`.* Single persistence step, no duplicated
  validation, no race window, no compensation. The grain already owns
  `LoadIssueSummaryAsync`; reusing it inside create is a minimal, symmetric
  change.

**Failure shape.** A new typed exception (e.g. `PrerequisiteValidationException`)
carrying the offending number and a reason (`not_found` / `self_reference`)
lets the route handler map to a precise `ApiResults.BadRequest(message, code)`
consistent with `AddPrerequisiteRequest`'s existing `404`/circular error
surface.

### D2 — De-duplicate at the domain boundary; preserve idempotent semantics

**Decision.** Collapse duplicate `prerequisiteNumbers` to a unique set at the
`CreateAsync` entry (or rely on `Issue.AddPrerequisite`'s existing
`Contains` guard — `Issue.Prerequisites.cs:11`). The spec scenario "Repeated
numbers collapse to a single prerequisite" must produce `prerequisiteNumbers:
[5]` in the response, not an error.

**Rationale.** Mirrors the single-add path's idempotency, so `[5,5,5]` at
create behaves like three `AddPrerequisiteAsync(5)` calls.

### D3 — Response is the full read model via the existing `IssueQuerier`

**Decision.** After a successful `CreateAsync` with prerequisites, the handler
continues exactly as today: `await issuesQuery.GetAsync(project.Id, number, project)`.
`IssueQuerier` already joins `prerequisites`, `canStart`, and `blocker` for
every issue read (they are populated today), so the `201` response body is the
full read model with no extra wiring. The dialog mutation's `onSuccess`
already reads `data.number` off this shape — extending `createIssue` to send
`prerequisiteNumbers` is a purely additive client change.

**Rationale.** No new response DTO, no special-cased read path. The spec's
"Response carries populated prerequisite and start-readiness read models"
falls out of the existing read model for free once `CreateAsync` persists the
prerequisites.

### D4 — One reusable `IssuePrerequisitePicker` feature component

**Decision.** Introduce a single React component — `IssuePrerequisitePicker` —
under a new `packages/web/src/features/prerequisite-picker/` slice (FSD
*feature* layer, because it composes the `entities/issue` list query with
selection UI and is consumed by two higher layers: the create-issue feature
and the issue-detail page). Its public API:

```ts
<IssuePrerequisitePicker
  projectId: string
  excludeNumbers: number[]            // current issue + already-selected
  selected: number[]                  // controlled selection
  mode: 'buffer' | 'live'             // buffer: create dialog; live: backlog editor
  onAdd(number: number): Promise<void>   // live mode: addPrerequisite
  onRemove(number: number): Promise<void>// live mode: removePrerequisite; buffer: local only
  renderChips?: boolean               // default true
/>
```

- **Candidate source:** the existing `getIssues({ projectId })` query (already
  used by the board). The picker filters client-side by number (substring on
  `String(number)`) and title (case-insensitive `includes`). No new endpoint.
  Each candidate renders `#number · title · status` and (if available) the
  completed/health badge from `IssuePrerequisiteSummary`.
- **Exclusions:** `excludeNumbers` is computed by the caller as
  `[currentIssueNumber, ...alreadySelectedNumbers]`. Cross-project exclusion is
  structural — `getIssues` is project-scoped.
- **Chips:** selected numbers render as removable chips beneath the search
  input. In `mode='buffer'`, removal mutates local state only. In
  `mode='live'`, removal calls `onRemove`, which the backlog editor wires to
  `removePrerequisiteMutation.mutateAsync`.

**Rationale.** One component, two modes — the spec mandates "the same picker
component for selecting prerequisite issues" in both surfaces. The `mode`
prop is the only seam between the create-dialog's buffered semantics and the
backlog editor's live add/remove contract; it keeps the picker's rendering and
search logic identical while localizing the contract difference.

**Alternatives considered.**

- *A1 — Two components sharing a hook.* More surface area, drift risk. Rejected.
- *A2 — Put the picker in `entities/issue/ui`.* Tempting, but the picker composes
  a React Query hook and selection state — it is a feature, not a pure entity
  view. Placing it in `entities` would invert the FSD dependency
  (`features/create-issue` and `pages/issue-detail` both consume it; the picker
  consumes `entities/issue`). The `features/prerequisite-picker` slice keeps
  the dependency direction clean: both consumers import downward into a
  sibling feature, which imports downward into `entities/issue`.

### D5 — Create dialog buffers selections locally; submit sends `prerequisiteNumbers`

**Decision.** `CreateIssueDialog` adds `const [prerequisiteNumbers, setPrerequisiteNumbers] =
useState<number[]>([])`, renders `<IssuePrerequisitePicker mode="buffer" selected={prerequisiteNumbers}
onAdd={n => setPrerequisiteNumbers(prev => [...prev, n])} onRemove={n => setPrerequisiteNumbers(prev =>
prev.filter(x => x !== n))} excludeNumbers={prerequisiteNumbers} />`, and on submit extends the
`createIssue` body with `prerequisiteNumbers` (only when non-empty). `resetAndClose`
clears `prerequisiteNumbers` alongside the other fields (spec: "Dialog resets
prerequisite selection after create").

The `createIssue` client (`client.ts:21`) gains a `prerequisiteNumbers?: number[]`
field; only the key's presence is required — the existing
`{ ...body }` spread already forwards it.

### D6 — Backlog editor swaps the numeric input for the picker; keeps the single-add/remove contract

**Decision.** `IssueConfigurationCard` removes the numeric `<Input>` + `Add`
button + `prereqError`/`handleAdd` block (`IssueConfigurationCard.tsx:27-75`)
and renders `<IssuePrerequisitePicker mode="live" selected={issue.prerequisites.map(p =>
p.number)} excludeNumbers={[issue.number, ...issue.prerequisites.map(p => p.number)]}
onAdd={(n) => addPrerequisiteMutation.mutateAsync(n)} onRemove={(n) =>
removePrerequisiteMutation.mutateAsync(n)} />`. The existing chip-removal block
(`:76-94`) is subsumed by the picker's own chips.

`addPrerequisiteMutation` / `removePrerequisiteMutation` (the existing clients
that hit `POST /issues/{n}/prerequisites` and `DELETE .../{pn}`) are passed
through unchanged. Server-side validation errors (nonexistent, self-reference,
"circular") surface through the mutation's `error` and are rendered by the
picker — preserving the exact error-surfacing behavior the numeric editor had.

**Rationale.** No new add/remove endpoint (spec: "Adding a prerequisite from
the backlog editor uses the existing add contract"). The picker is purely a
selection UI layered over the unchanged single-add contract.

### D7 — Incomplete-prerequisite / Start-eligibility messaging reuses the existing read model

**Decision.** The picker renders, per selected prerequisite, a small
incomplete indicator when `prereq.completed === false`, plus a single
line at the section boundary that summarizes the issue's `canStart` / `blocker`
(e.g. "Cannot start: waiting on #5"). Both fields come straight off
`Issue.prerequisites` and `Issue.canStart` / `Issue.blocker` already in the
read model — no new readiness model, no new query.

## Risks / Trade-offs

- **[Counter slot burned on failed create]** `IssueCounterGrain.NextAsync`
  writes eagerly, so a create that fails prerequisite validation still
  increments the counter. -> Acknowledged as **established counter semantics**
  by the spec ("the issue counter's next allocation SHALL reflect that no
  successful creation consumed the rejected number's slot per the established
  counter semantics"). Same behavior as today's attachment/profile validation
  failures. No mitigation required; documented for reviewers.
- **[Self-reference only — no deep cycle detection]** The existing invariant
  (`Issue.AddPrerequisite` + grain) only rejects a prerequisite equal to the
  target issue's own number; it does **not** graph-walk for A→B→A cycles. The
  create path inherits this. -> Out of scope per Non-Goals; the spec's
  "circular dependency handling" maps to the existing self-reference guard
  (the `"circular"` error string in the Web UI today actually fires only on
  self-reference, see `IssueGrain.cs:533-534`). Deep cycle detection is
  tracked under Open Questions.
- **[Picker list performance]** The picker filters the full project issue list
  client-side. For projects with thousands of open issues this could lag. ->
  The board already loads the same list; reuse is cheap. If perf becomes an
  issue, add input debouncing + result capping (e.g. top 50 by relevance)
  without changing the contract. Listed as a follow-up, not a blocker.
- **[Live-mode race: picker offers an issue deleted between list fetch and
  add]** The candidate list is a point-in-time snapshot; the user could pick a
  number that gets deleted before `addPrerequisite` lands. -> The server's
  existence check still rejects it; the picker surfaces the mutation error.
  This is the same behavior as the numeric editor today, just less likely
  because the user picks from real candidates.
- **[FSD placement of the picker]** A new `features/prerequisite-picker`
  slice adds a directory. -> Justified by D4's dependency-direction argument;
  the slice is small (one component + a test).
- **[Backward compatibility of `CreateAsync` signature]** Adding a parameter
  to the grain interface changes the Orleans call surface. -> The project is
  pre-release ("无需考虑版本兼容", AGENTS.md). All callers are in-repo and
  updated atomically. Optional parameter + default `null` keeps the unmodified
  call sites valid.

## Migration Plan

- **No data/schema migration.** Prerequisites are already a persisted field on
  `Issue`; this change only populates them at create time and improves the
  editor UX. No DB column, no new table, no event type.
- **Deploy order.** Server and Web ship together. The server's
  `prerequisiteNumbers` field is optional and additive — an older Web client
  posting the old body still works unchanged. A newer Web client posting
  `prerequisiteNumbers` against an older server is impossible in this repo's
  single-ship model, and the field would be silently ignored by older DTO
  binding anyway (no error).
- **Single-add/remove contract unchanged.** The backlog editor's swap to the
  picker is a pure UI change; the HTTP traffic shape is identical to today.
- **Rollback.** Revert the commit. Created issues with create-time
  prerequisites remain fully readable (the prerequisites are persisted as
  normal); only the create-dialog field and the picker UI disappear. No data
  repair needed.

## Open Questions

- **Deep cycle detection?** The current invariant is self-reference only
  (`prerequisiteNumber == _issue.Number`). Should create-with-prerequisites
  also reject A→B→A cycles? The proposal scopes this out ("rejects
  self/circular dependencies where applicable" is hedged), and the existing
  single-add path does not enforce deep cycles either. **Default: keep parity
  with the single-add path (self-reference only) to avoid diverging
  create-time and edit-time semantics.** Revisit if a cycle surfaces in
  practice.
- **Should `CreateIssueRequest.PrerequisiteNumbers` cap its length?** A
  pathological request could send thousands. The grain's per-prereq existence
  check is one DB read each. -> Consider a soft cap (e.g. 50) at the route
  handler if profiling shows it's needed; not blocking for the initial ship.
- **Picker debouncing / result cap.** See Risks. Decide based on real project
  sizes; ship without first.
