# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: `proposal.md` Impact line asserted "model + Orleans grain/storage" as
  the server-side persistence mechanism, which contradicts `design.md` decision D1
  that selects EF Core (1:1 with `Projects`, scoped `InboxSubscriptionStore :
  IScopedService`) with documented rationale and a rejected-grain alternative. The
  two artifacts in the same change disagreed on storage. Changed `proposal.md` to
  read "model + durable storage", making the proposal implementation-agnostic (the
  proposal is the "what"; the storage "how" is decided by D1). No architectural
  change — the decision in D1 is untouched, and `tasks.json` (T-001) already
  mandates the EF table / scoped store / migration.
  Verification: Re-read `proposal.md` Impact, `design.md` D1, and T-001
  description/acceptance; they now agree that durable project-scoped preference
  state is persisted via an EF Core table and scoped store, with the proposal no
  longer asserting a conflicting mechanism.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Open Question #1 asks whether disabling a kind should
  invalidate the unread-count/badge query. T-004 notes the intended answer (do NOT
  invalidate `['inbox', projectId]`), which is consistent with "preferences affect
  future projection only" and "existing items are unchanged." This is a design open
  question carried into implementation, not a plan defect.
  SuggestedAction: Confirm the no-invalidation decision during T-004 implementation
  and, if still valid, leave the inbox list / unread-count query keys untouched.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: `design.md` Open Question #2 (exposing subscription state via SignalR
  for live cross-client updates) is explicitly out of scope and the read query
  refetches on settings mount. No task covers it, correctly.
  SuggestedAction: No action for this issue; revisit only if multi-client live
  preference sync becomes a requirement.
  Status: follow-up

## Review Dimensions Summary

- **Alignment:** The proposal addresses the actual issue (project-scoped inbox
  subscription preferences over the four MVP notification kinds). Every "What
  Changes" entry traces to an issue requirement: the `InboxSubscription` model over
  the four kinds (Domain Model), all-enabled default preserving MVP behavior
  (Acceptance Criteria #3), future-only projection with no retroactive mutation
  (Acceptance Criteria #4–#6), project-scoped read/update API (Acceptance Criteria
  #1–#2), and the Web UI surface with product labels (Acceptance Criteria #7). All
  issue Non-Goals (no per-user prefs, no per-issue watch, no label/stage rules, no
  global default UI, no retroactive deletion/backfill, no external channels) are
  respected — none appears in any spec or task. The "separate from SignalR
  connection subscriptions" Domain Model rule has a dedicated spec requirement.

- **Completeness:** All nine issue Acceptance Criteria are covered. Projection
  test coverage (enabled / disabled / missing-default / re-enabled) is mandated by
  T-002 acceptance criteria; API and UI read/update test coverage is mandated by
  T-003 and T-004. Every spec requirement maps to at least one task: the model
  requirement → T-001; the all-enabled default → T-001 (store synthesizes
  default); product-state-separate-from-realtime → satisfied by T-001/T-002
  architecture (subscription code never references SignalR); the HTTP API → T-003;
  the Web UI → T-004; the modified subscription-gated projection → T-002. Edge
  cases (missing-row default, re-enable-no-backfill, existing-items-untouched,
  unknown-key rejection, cross-project isolation, replay idempotency on a skipped
  event) are addressed in spec scenarios and task acceptance criteria.

- **Consistency:** After item-1, `proposal.md` Capabilities (`project-inbox-
  subscription` new, `project-inbox` modified), the five spec requirements, design
  decisions D1–D6, and tasks T-001–T-004 use consistent naming (`InboxSubscription`,
  `NotificationKind`, the four kind strings `workflow_failed` /
  `approval_requested` / `issue_started` / `issue_completed`, `InboxSubscriptionStore`,
  `InboxProjectionHandler`, `InboxRoutes`, `KIND_DESCRIPTORS`). Spec anchors in
  `tasks.json` use the repo's mixed-case convention and match the actual requirement
  headings. The MODIFIED `project-inbox` spec reuses the baseline requirement
  heading and adds subscription-gated scenarios, matching the `project-inbox`
  capability delta. Design D6's "new `inbox` tab, not `PreferencesSection`" is
  consistent with the spec's "project settings or inbox settings surface."

- **Feasibility:** Codebase references underpinning the design were verified:
  `NotificationKinds` exposes exactly the four kind strings
  (`InboxModels.cs:8-13`); `InboxProjectionHandler.ProjectAsync` opens its DI scope
  at `:114`, knows `kind` from `:111` and `resolved` by `:131`, and inserts at
  `:149` before publishing the `InboxItemPersisted` hint at `:176-179` — exactly
  the seam D4/T-002 describe; the four authoritative events map at `:118-129`. The
  store will auto-register via `IScopedService` (D1). All dependencies either exist
  in the codebase or are created by T-001 (the shared foundation consumed by T-002
  and T-003). No circular dependencies. Task granularity is appropriate: each task
  is a cohesive feature slice (persistence foundation; projection gate; HTTP API;
  Web UI), none is titled as a pure technical action ("define interface" / "extract
  class" / "register DI"), and tests are embedded inside each implementing task
  rather than split into standalone test tasks.

- **Dependency completeness:** T-001 has `dependsOn: []` (priority 1, the
  foundation). T-002 → T-001 (priority 2 > 1). T-003 → T-001 (priority 2 > 1).
  T-004 → T-003 (priority 3 > 2); T-004 does not depend on T-002, which is correct
  because the Web UI consumes only the HTTP API contract, not the projection gate
  (and T-003 transitively ensures T-001 is done). Every `dependsOn` points to an
  existing ID with strictly lower priority, and the directed graph (T-001 ←
  {T-002, T-003} ← T-004) is acyclic.

<promise>PASS</promise>
