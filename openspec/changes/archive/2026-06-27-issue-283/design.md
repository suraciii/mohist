## Context

An Epic is a product goal; its linked issues are the execution plan for that goal. Today an Epic can only become `done` when **every** linked issue is *delivered* (`done`/`completed`). When linked issues are split between `done` and `cancelled`, the Epic is effectively finished (every issue has reached a terminal state, none has further execution) but is stuck because `cancelled` counts against the readiness rule.

The readiness rule is currently computed in **two** places that should agree but encode the rule differently, and the divergence is the root cause of the bug:

1. **Read model** — `EpicProgress.Build` (`packages/server/src/Mohist.Server/Epic/Services/EpicProgress.cs:37`):
   `ReadyToMarkDone = linked.Count > 0 && completed.Count == linked.Count`. This requires *all delivered*, so any `cancelled` remaining keeps it `false`.
2. **Command/grain side** — `EpicGrain.ComputeUndeliveredLinkedNumbersAsync` (`packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:431`):
   treats *undelivered* as `!IsCompleted`, i.e. anything that is not `done`/`completed`. A `cancelled` issue is therefore `undelivered`, so `undelivered.Count > 0` blocks `MarkDone`, auto-done, and reconcile.

Note the advancement path already does the right thing: `EpicProgress.Build` line 15 and `EpicGrain.TryStartNextAsync` line 378 both compute `!IsCompleted && status != "cancelled"` (open candidates only) for next-issue selection. So the system already has a correct notion of "open"; the **readiness** sites just fail to reuse it.

Constraints:

- `LinkedIssueDto.Status` values produced by `MohistDefaultWorkflowProjection.IssueStatusName` are `backlog`, `in_progress`, `done`, `cancelled` (`IsCompleted` also tolerates a defensive `completed`). `draft`/`blocked`/`paused` are **Health** facets of an open issue, not lifecycle statuses — so the open/terminal boundary is drawn purely on `Status`.
- No schema/persistence changes (per issue non-goals). No new Epic or Issue statuses.
- The codebase is pre-1.0 and actively developed; backward API compatibility is not a constraint.

Stakeholders: Epic lifecycle (auto-done, manual Mark Done, resume), epic list/detail read models, CLI (`mo epic done`), Web UI ready-to-done indicator.

## Goals / Non-Goals

**Goals:**

- Define Epic readiness as "has ≥1 linked issue **and no open linked issue**" where *open* = not terminal = `Status` not in `done`/`completed`/`cancelled`. A `cancelled` linked issue is terminal and non-blocking but is **not** counted as delivered.
- Drive auto-done, manual Mark Done, resume re-evaluation, and the read-model `readyToMarkDone` from **one** shared computation so they cannot diverge again.
- Keep `deliveredCount` semantics unchanged (counts `done`/`completed` only).
- Express the change in tests using terminal/open domain language; cover the mixed `done`+`cancelled` (Epic #18) case becoming completable.

**Non-Goals:**

- No new Epic or Issue states; no change to `done`/`cancelled` status semantics.
- No change to `deliveredCount` meaning or to next-issue selection ordering.
- No change to Epic *close* non-destructive semantics (tracked by #179).
- Not renaming `undelivered` in the unrelated Issue **prerequisite** domain (`IssueGrain.LoadUndeliveredPrerequisiteNumbersAsync`, `Issue.StartBlocker(undeliveredPrerequisites)`, `EpicGrain.BuildLinkedIssueDtosAsync`'s `undeliveredPrereqNumbers`). Those describe issue prerequisites, not Epic readiness, and are out of scope.

## Decisions

### D1. One shared readiness predicate on `LinkedIssueDto`

Add to `EpicProgress`:

- `IsTerminal(LinkedIssueDto)` = `IsCompleted(i) || i.Status == "cancelled"` (reuses existing `IsCompleted`).
- `IsOpen(LinkedIssueDto)` = `!IsTerminal(i)`.
- `IsReadyToComplete(IReadOnlyList<LinkedIssueDto>)` = `linked.Count > 0 && !linked.Any(IsOpen)`.

**All three readiness consumers funnel through these:**

- `EpicProgress.Build`: `ReadyToMarkDone = IsReadyToComplete(linked)` (replacing `completed.Count == linked.Count`).
- `EpicGrain.ComputeUndeliveredLinkedNumbersAsync`: select numbers where `IsOpen(dto)` (replacing `!IsCompleted(dto)`). The resulting set — now genuinely the *open* set — is passed to `MarkDone(openLinkedNumbers)` and used as the reconcile/auto-done gate (`open.Count == 0`).
- `Epic.MarkDone`: keeps its aggregate-level guard `openLinkedNumbers.Count > 0 ⇒ throw`, as defense-in-depth at the aggregate boundary (consistent with the execution-fact/state-adjudication split in `design/architecture.md`).

*Rationale:* the advancement path already computes the equivalent of "open" locally; this just promotes it to a named, shared predicate so readiness cannot drift from selection again.

*Alternatives considered:*
- Enumerate `IsOpen` as `status in {backlog, in_progress}` instead of `!IsTerminal`. Rejected: `!IsTerminal` is robust against the defensive `completed` alias and requires no edit if a future terminal alias appears. The issue explicitly forbids new states, so either is safe; `!IsTerminal` is the smaller, less error-prone surface.
- Compute readiness by reusing the existing `undelivered` local in `EpicProgress.Build` (which is already `!IsCompleted && != cancelled`). Rejected as the *sole* mechanism: it is local to `Build` and would still leave the grain computing its own copy. A named static predicate is the single source of truth both call.

### D2. Rename the readiness "undelivered" concept to "open" (Epic scope only)

The word `undelivered` is the wrong domain term for readiness — a `cancelled` issue is neither delivered nor "undelivered work waiting to happen"; it is out of scope. The acceptance criterion requires terminal/open domain language. Rename within the **Epic readiness path only**:

- `Epic.MarkDone(undeliveredLinkedNumbers)` → `MarkDone(openLinkedNumbers)`.
- `EpicNotReadyToMarkDoneException`: property `UndeliveredCount` → `OpenLinkedCount`; message → "…has {n} open linked issue(s)…".
- `EpicGrain.ComputeUndeliveredLinkedNumbersAsync` → `ComputeOpenLinkedNumbersAsync` (and its local/call sites at lines 242, 334, 417).
- `EpicProgress.Build` local `undelivered` (the advancement candidate list) → `open` (it already means open candidates; renaming removes the last false friend).
- `EpicRoutes.cs:131` API payload field `undeliveredCount` → `openCount`; error code `EPIC_NOT_READY_TO_MARK_DONE` is **unchanged**.

*Rationale:* the spec mandates domain language; the repo is monorepo + pre-1.0, so an API payload field rename is cheap and keeps the term honest. The Issue prerequisite `undelivered*` identifiers are deliberately left alone (D-scope boundary).

*Alternatives considered:*
- Keep `undeliveredCount` in the API payload for stability. Rejected: it would misname a count that now excludes `cancelled`, which is the exact confusion this change removes. Keeping the error *code* stable preserves client branching logic; only the diagnostic field name changes.

### D3. `deliveredCount` is untouched

`EpicProgress.Build` continues to set `DeliveredCount = completed.Count` (`IsCompleted` = `done`/`completed` only). `cancelled` is terminal for *readiness* but never delivered. This preserves the epic-list-query contract (`deliveredCount`/`totalIssueCount` semantics) and requires no read-model change beyond `ReadyToMarkDone`.

### D4. No migration; reconcile is self-healing

No persisted field changes. Any Epic currently stuck in `idle`/`running` with all-linked-terminal becomes completable on the next terminal event (auto-done) or the next Mark Done / resume. The existing `IssueClosed` (`EpicClosedReconcileHandler`) and `IssueWorkCompleted` (`EpicAutoDoneHandler`) subscriptions already route both terminal signals through `ReconcileAfterTerminalAsync`, so a `cancelled` event already triggers re-evaluation; after D1 it will resolve to `done`.

## Risks / Trade-offs

- `[Stale stuck epic won't self-heal without a trigger]` -> An Epic that was *already* all-terminal before deploy and receives no further events stays `idle`/`running` until a manual Mark Done / resume / re-link touch. *Mitigation:* documented in release notes; the detail page will now show `readyToMarkDone: true`, guiding the user to Mark Done. A background sweep is explicitly out of scope (would need scheduling infra).
- `[Payload field rename breaks unknown clients]` -> `undeliveredCount` → `openCount` in the 409 body. *Mitigation:* error *code* `EPIC_NOT_READY_TO_MARK_DONE` is unchanged; only the diagnostic detail field is renamed. CLI and Web are in-repo and updated in the same change.
- `[Confusing two meanings of "undelivered"]` -> The Issue prerequisite path retains `undeliveredPrerequisites` naming. *Mitigation:* D2 scope note documents the boundary; the two concepts live in different domains (Epic readiness vs Issue prerequisites) and are not adjacent in code.
- `[New terminal status introduced later]` -> A future Issue status (e.g. a "skipped") would be misclassified as open by `IsOpen = !IsTerminal`. *Mitigation:* the issue explicitly forbids new statuses; if one is added, `IsTerminal` must be updated in exactly one place.

## Migration Plan

1. Implement D1–D3 (predicate, rename, `Build`/grain/exception/routes updates).
2. Update tests: rewrite assertions encoded with the old "cancelled blocks" semantics in `EpicProgressSpecs`, `EpicProgressBuildSpecs`, `EpicTransitionsSpecs`, `EpicAutoDoneHandlerSpecs`, `EpicLifecycleSpecs`, `EpicReconciliationServiceSpecs`, and `CliEpicCommandSpecs`; add explicit coverage for the mixed `done`+`cancelled` epic reaching `done` (the Epic #18 scenario) and for `cancelled`-only-remaining being `readyToMarkDone`.
3. Run `npm test` (server; `TreatWarningsAsErrors` acts as lint) and `npm run test:run -w packages/web` (Web consumer of the 409 payload).
4. Deploy is a normal server restart (`mo update server`); no DB migration. Rollback is a revert + restart — no data shape depends on the change.

## Open Questions

- Do we want a one-time startup sweep that auto-marks-done any existing `idle`/`running` epic whose linked issues are already all-terminal, so users don't have to touch each stuck epic manually? (Currently out of scope per D4; deferred pending feedback after deploy.)
