## Context

`Issue` is a `partial class` domain entity (`packages/server/src/Mohist.Server/Issue/Domain/Issue.cs`) whose state is persisted as a serialized blob (`Issues.State`) via `IssueStore.Serialize/Deserialize` (`Infrastructure/Data/Issue/IssueStore.cs`), with prerequisite links in a separate `IssuePrerequisites` table. Issue-level start readiness is currently *not* on the entity:

- `Issue.Transitions.cs:63` `StartWorkflow()` only rejects `Cancelled`/`Done`/already-running — every backlog issue is startable regardless of completeness.
- Start readiness lives in `IssueStartEligibility` (`Issue/Services/IssueInfo.cs:73`): `{ bool Startable, string Reason, string? Message, Prerequisite[] WaitingForCompletion }`, computed in two places — `IssueGrain.GetStartEligibilityAsync()` (`IssueGrain.cs:378`) for the start path, and `IssueQuerier` (`IssueQuerier.cs:356`) for list/detail reads.
- The start flow (`Api/IssueRoutes.Lifecycle.cs:29` → `IssueGrain.StartWorkAsync` `:108` → `Issue.StartWorkflow`) pre-checks eligibility in the route, re-checks in the grain, then the domain checks execution status — three places, none of them authoritative for draft/prerequisites.

Constraints: `IssueStatus` execution lifecycle is fixed (`Backlog → InProgress → Done | Cancelled`); no new board columns; `IsDraft` is a bool, not an enum. Stakeholders: board/detail UI (`web-ui`), CLI (`cli-interface`), HTTP API consumers.

## Goals / Non-Goals

**Goals:**
- Add an authored `IsDraft` flag to the `Issue` entity; new issues default to draft.
- Make `Issue.Start()` the single authority for all start preconditions (draft, prerequisites, execution status, no active run), reporting a concrete typed blocker.
- Expose "can start?" / "what's blocking it?" as a derived query on the `Issue` itself; delete `IssueStartEligibility` and its calculators.
- Replace the API `startEligibility` / `waitingForDelivery` fields with `isDraft` / `canStart` / `blocker`.
- Migrate existing backlog issues to ready so the change does not retroactively suppress actionable work.

**Non-Goals:**
- A multi-value readiness enum; auto-deriving draft-ness from body completeness; changing `IssueStatus`; new board columns or a triage workflow.

## Decisions

### Decision 1: `IsDraft` is an authored field on the `Issue` entity, persisted via the existing state blob

Add a private backing field `_isDraft` + property to `Issue.cs`, mirroring the existing `_status` / `_prerequisiteNumbers` pattern. `Issue.Create()` (`Issue.Transitions.cs:7`) sets `IsDraft = true` for new issues. Add a `SetDraft(bool, now)` command (records an `IssueDraftChanged` event for symmetry with other transitions). No new persistence column is needed — the field round-trips through the existing serialized `Issues.State` blob.

- *Alternative considered:* a dedicated `is_draft` SQL column. Rejected — issues are document-stored as a state blob and no other scalar issue field has its own column; adding one would split the issue's state across two stores.

### Decision 2: Migration defaults existing issues to ready via field default

The `_isDraft` backing field defaults to `false`. Because `IssueStore.Deserialize` reconstructs entities from JSON, existing rows that predate the field deserialize with the default (`false` = ready); only `Create()` explicitly sets `true`. This satisfies "existing backlog issues migrate to ready" with zero data backfill.

- *Alternative considered:* backfill all rows to a sentinel then interpret. Rejected — needless complexity for a single bool with a safe default.

### Decision 3: A typed `IssueStartBlocker` sum replaces the eligibility object; one private computation

Introduce a domain type, e.g. `abstract record IssueStartBlocker { record Draft; record WaitingFor(IssueRef Prerequisite); }` (none = `null`). `Issue` exposes:
- `IssueStartBlocker? StartBlocker(IReadOnlySet<int>? undeliveredPrerequisites)` — derived query, never authored.
- `bool CanStart(...) => StartBlocker(...) is null;`
- `Start()` enforces via the same private `ComputeBlocker(...)` and throws a typed `IssueStartBlockedException(IssueStartBlocker)` when blocked (in addition to the existing execution-status checks).

`IssueStartEligibility`, `FromPrerequisites`, `Ready`, and all `[GenerateSerializer]` decoration on it are deleted. `IssueReadModel.StartEligibility` and `IssueInfo` exposure are removed.

### Decision 4: Prerequisite delivery is fed into the domain; the Issue owns blocker precedence

A subtlety: the `Issue` aggregate only stores `PrerequisiteNumbers`. Whether a prerequisite is *delivered* is the state of *other* issues (loaded today by `IssueGrain.LoadIssueSummaryAsync` and by `IssueQuerier` lines 320-356). The domain therefore cannot self-evaluate delivery without that data. Decision: the orchestrators (grain/querier) gather the **set of undelivered prerequisite numbers** and pass them into `StartBlocker(undeliveredPrerequisites)`. The `Issue` remains the authority for **which** blocker applies and **precedence** (Draft takes priority over WaitingFor). This honors "behavior lives on the Issue" while acknowledging prerequisites are inherently cross-aggregate.

- *Alternative A (rejected):* keep a service-level calculator, just rename it. This is exactly the anemic-model split the issue rejects.
- *Alternative B (rejected):* Issue owns only the draft check; the grain owns the prerequisite check. Splits start knowledge across two layers again — the root cause being fixed.

### Decision 5: Single authority for the start path

Consolidate the three current checks into one. `IssueGrain.StartWorkAsync` computes `undeliveredPrerequisites` (existing prerequisite-summary loading), calls `Issue.Start(wrId, undeliveredPrerequisites)`, and the domain enforces draft → prerequisites → execution status → active run in that order, throwing `IssueStartBlockedException`. The route handler (`IssueRoutes.Lifecycle.cs:29`) replaces `GetStartEligibilityAsync()` + `StartWorkAsync()` with a single grain call that returns the typed blocker on refusal (the route maps it to a 400 with `canStart`/`blocker`). The grain keeps a read-through `GetStartReadinessAsync()` only to serve the list/detail querier.

- *Alternative considered:* keep the route pre-check + domain throw as two separate calls. Rejected as the *sole* mechanism because it requires a try/catch to produce structured output; instead the grain returns the blocker as data on the refusal path and `Start()` remains the enforcing command (defense in depth — both route the same private `ComputeBlocker`).

### Decision 6: API contract change is breaking, surfaced as `isDraft`/`canStart`/`blocker`

On `IssueReadModel` and `IssueInfo`: add `bool IsDraft`, `bool CanStart`, and `Blocker` (`{ kind: "draft" }` | `{ kind: "waiting-for", issue: { number, ... } }` | `null`). Remove `StartEligibility`. `CreateIssueRequest`/`UpdateIssueRequest` (`IssueRoutes.Dtos.cs`) gain `bool? IsDraft` (omitted-on-create ⇒ draft). `IssueQuerier` computes `CanStart`/`Blocker` where it previously called `IssueStartEligibility.FromPrerequisites` (`:356`).

## Risks / Trade-offs

- `[Risk]` Serializer round-trip of the new `_isDraft` field — `IssueStore` uses `JSON.Serialize/Deserialize`; if the configured `System.Text.Json` options ignore default values or the Orleans `[GenerateSerializer]`/`[Id]` set is used for another path, an old row could mis-deserialize. -> `Mitigation`: confirm `IsDraft` is emitted on serialize (it is non-default `true` for new issues, so it is always present going forward); add a serialization round-trip unit test for the issue state blob.
- `[Risk]` Breaking API change for any external consumer reading `startEligibility`. -> `Mitigation`: this is an internal local tool (single CLI + web client); both are updated in the same change. No external SLA.
- `[Risk]` Cross-aggregate prerequisite read on every list/detail query to compute `blocker` could add latency. -> `Mitigation`: the prerequisite summary load already happens today (`IssueQuerier:320`); `CanStart`/`Blocker` reuse that data, adding only a cheap set lookup and the draft check. No new query.
- `[Risk]` Draft default surprises users who script `mo issue create` then immediately `start`. -> `Mitigation`: create output explicitly guides "mark ready" before start; CLI gains a ready flag on create/update (per `cli-interface` spec).
- `[Trade-off]` Passing `undeliveredPrerequisites` into the domain slightly leaks the "delivery" concept into the entity method signature. Accepted: the alternative (a calculator type) is worse, and the Issue still owns precedence and the blocker shape.

## Migration Plan

1. **Domain + persistence first:** add `IsDraft` (default `false`), `SetDraft`, `StartBlocker`/`CanStart`, and `Start()` enforcement on `Issue`. Existing rows deserialize to ready (Decision 2).
2. **Retire eligibility:** delete `IssueStartEligibility`; rewrite `IssueGrain.GetStartEligibilityAsync`/`StartWorkAsync` and `IssueQuerier:356` to the new query/path.
3. **API + DTOs:** swap response/request fields (Decision 6); update start handler to return the typed blocker.
4. **Clients:** update `web-ui` (board/detail draft indicator, Start disable) and `cli-interface` (create default draft, mark-ready, start tip).
5. **Tests:** domain blocker/Start tests; start-handler draft/prerequisite refusal; querier `canStart`/`blocker` projection; serialization round-trip; CLI create-defaults-to-draft.
- **Rollback:** revert is code-only — no schema change was made, and because `IsDraft` deserializes to `false` (ready) when absent, reverting leaves all issues startable as before. The only rollback artifact is serialized `IsDraft` values in the state blob, which are ignored by the old code.

## Open Questions

- Exact `blocker` JSON wire shape for `WaitingFor(Issue)` — full prerequisite summary vs. minimal `{ number, title }`? Leaning minimal; confirm against what `web-ui`/CLI need to render.
- Should `SetDraft` be exposed as its own endpoint (`POST /issues/:n/ready`) or only via `PATCH` `isDraft`? Currently spec'd as PATCH only; revisit if the UI wants a dedicated action.
- Whether `Issue.Start()` should return a `StartResult` instead of throwing `IssueStartBlockedException` to avoid exception-as-control-flow on the expected refusal path. Decide during implementation once the grain/route boundary is wired.
