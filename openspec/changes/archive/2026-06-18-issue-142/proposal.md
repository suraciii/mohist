## Why

The backlog cannot tell a half-written stub from a fully specified, pickable issue — every backlog issue is equally startable. Worse, the model answers "can this start?" with an over-abstracted external type, `IssueStartEligibility` (`{ bool Startable, string Reason, string? Message, Prerequisite[] WaitingForCompletion }`): a redundant bool fully determined by `Reason`, stringly-typed cases, and UI text duplicating a data array. The `Issue` is anemic about its own start preconditions — `Start()` only checks execution status while draft/prerequisite knowledge leaked into a parallel calculator type.

We need an authored draft flag so the board reflects what is genuinely pickable, and the start-readiness knowledge must move back onto the `Issue` itself.

## What Changes

- Add an authored `IsDraft` flag to `Issue`. **New issues default to draft** — explicit "mark ready" is required before the board treats an issue as pickable.
- A draft issue cannot be started; `Issue.Start()` now enforces all start preconditions (draft, prerequisites, execution status, no active run) and reports the concrete blocker.
- Move "can this start?" / "what's blocking it?" onto the `Issue` as a thin derived query: `CanStart = !IsDraft && prerequisites complete`, with a concrete `Blocker` that is one of `Draft | WaitingFor(Issue) | none` — a sum of real cases, not a `{ bool, reason-string }` envelope.
- **BREAKING**: Remove the `IssueStartEligibility` type and its calculator. The API exposes a shallow derived `canStart` / `blocker` shape instead of an eligibility object with a `Reason` string.
- **BREAKING**: Replace the API response fields `startEligibility` / `waitingForDelivery` with `isDraft`, `canStart`, and `blocker` across issue list and detail. Existing prerequisite-based blocking is now expressed as the `WaitingFor(Issue)` blocker case.
- Issue create/update accept `isDraft`; create defaults to draft.
- The board and the issue detail card visually distinguish draft from non-draft backlog issues (e.g. a dimmed "Draft" indicator), and the Start control is disabled with the concrete reason for drafts.

## Capabilities

### New Capabilities
- `issue-start-readiness`: The issue owns its own start-readiness. Covers the authored `IsDraft` flag (default draft), the derived `CanStart` / `Blocker` query (`Draft | WaitingFor(Issue) | none`), and `Issue.Start()` enforcing all start preconditions and reporting the concrete blocker. This is where draft state and the retirement of the external eligibility calculator are specified.

### Modified Capabilities
- `issue-prerequisites`: The "start eligibility summarizes whether an Issue may enter the pipeline" requirement is removed — that concept is retired and replaced by the issue-owned `canStart` / `blocker` (defined in `issue-start-readiness`). Prerequisite declaration and delivery evaluation remain; a non-delivered prerequisite now surfaces as the `WaitingFor(Issue)` blocker case rather than a standalone `startEligibility` / `waitingForDelivery` object.
- `http-api`: Replace `startEligibility` / `waitingForDelivery` response fields with `isDraft`, `canStart`, and `blocker` on issue list and detail. The start handler rejects drafts and prerequisite-waiting issues with the concrete blocker; issue create/update accept `isDraft` and create defaults to draft.
- `web-ui`: Board cards and the issue detail card show a draft indicator and disable Start with the concrete reason for drafts; card/list waiting reasons render from `blocker` instead of `startEligibility.waitingForDelivery`.
- `cli-interface`: `mo issue` create/show/list and the `mo issue start` flow render and respect `isDraft` / `canStart` / `blocker` (including create defaulting to draft and the start tip honoring draft state) instead of `startEligibility` / `waitingForDelivery`.

## Impact

- **Domain model** (`packages/server/src/Mohist.Server/Issue/Domain/`): add `IsDraft` to `Issue` (default draft on `Create`, `Issue.Transitions.cs:7`); extend `StartWorkflow`/`Start` (`Issue.Transitions.cs:63`) to enforce draft + prerequisites and return the concrete blocker; add `CanStart` / `Blocker` (`Draft | WaitingFor(Issue) | none`) as a derived query.
- **Eligibility type removal** (`packages/server/src/Mohist.Server/Issue/Services/IssueInfo.cs:73`): delete `IssueStartEligibility` and its `FromPrerequisites` / `Ready` calculators; replace `IssueInfo` exposure of eligibility with `isDraft` / `canStart` / `blocker`.
- **HTTP API** (`Issue` list/detail/start handlers, create/update DTOs): migrate response and request contracts off `startEligibility` / `waitingForDelivery` to `isDraft` / `canStart` / `blocker`; start endpoint reports the draft and prerequisite blockers.
- **Persistence/migration**: store `IsDraft`; default existing backlog rows to non-draft (ready) so the change is non-breaking for current items while new issues default to draft.
- **Web UI**: board and Issue Detail draft indicator + Start-disable reason; consume `blocker` instead of `startEligibility.waitingForDelivery`.
- **CLI**: `mo issue create/show/list/start` consume `isDraft` / `canStart` / `blocker`; create defaults to draft; start tip honors draft state.
- **Specs**: 1 new (`issue-start-readiness`) + 4 modified deltas (`issue-prerequisites`, `http-api`, `web-ui`, `cli-interface`).
