## Why

Mohist's runtime already understands issue prerequisites — `IssueGrain.AddPrerequisiteAsync` validates them, `GetStartReadinessAsync` produces the waiting/blocker read model, and `StartWorkAsync` blocks start when prerequisites are incomplete. But the two places a user actually declares dependencies cannot populate that model: the New Issue dialog has no prerequisite field at all, and the backlog detail editor only accepts a bare `Issue #` typed from memory. Users must create an issue first, then hunt for exact numbers, hitting `Issue #99999 not found` along the way. This makes dependency planning — the moment work is decomposed — the hardest time to model dependencies, which is precisely when it is most valuable. The gap is a create/edit UX and create-API-contract gap, not a missing domain capability, so it can be closed without changing start-blocking semantics.

## What Changes

- Add an optional `Prerequisites` field to the New Issue dialog (`CreateIssueDialog.tsx`), shown as removable chips before submission.
- Introduce a reusable, project-scoped **issue picker** that searches/selects existing issues by number, title, and status (with repository/project context), and replaces the numeric-only `Add Prerequisite` input in the backlog detail editor (`IssueConfigurationCard.tsx`).
- The picker excludes the current issue, already-selected prerequisites, and cross-project choices.
- **The create issue API accepts prerequisites atomically with issue creation**: `CreateIssueRequest` gains a prerequisite-numbers field; the create endpoint validates that every referenced issue exists in the selected project and rejects self-references before the issue is persisted, returning clear validation errors without leaving a partially configured issue.
- Created issues are returned with `prerequisiteNumbers`, `prerequisites`, and the start-eligibility/waiting read models already populated (no second round-trip required).
- The UI explains when a selected prerequisite is incomplete and how it affects Start eligibility, reusing the existing start-readiness model rather than introducing a new one.
- No change to task-level `dependsOn` inside `tasks.json`, to start-blocking semantics, or to the existing single-prerequisite add/remove HTTP contract (the backlog editor keeps using it behind the new picker).

## Capabilities

- `issue-create-prerequisites`: The create-issue path accepts zero or more prerequisites atomically with creation. Covers the server create API contract (new `CreateIssueRequest` field, project-scoped existence/self-reference validation, no partial issue on validation failure, populated read models in the response) and the New Issue dialog prerequisite field with removable chips.
- `issue-prerequisite-picker`: A searchable, project-scoped issue selection component reused by both the New Issue dialog and the backlog `Add Prerequisite` editor. Covers search by number/title/status with repository/project context, exclusion of the current issue / already-selected prerequisites / cross-project choices, and selection presented as removable chips. This is the capability that retires the numeric-only prerequisite editor.

## Impact

- **Server** (`packages/server`):
  - `Api/IssueRoutes.Dtos.cs` — add a prerequisite-numbers field to `CreateIssueRequest`.
  - `Api/IssueRoutes.Crud.cs` — after issue creation, apply prerequisites atomically; validate existence in the project and reject self-reference before persisting/returning; ensure the response includes populated `prerequisites` and start-eligibility. Reuse the existing `IIssueGrain.AddPrerequisiteAsync` validation path rather than duplicating self/circular logic.
  - No domain-layer change expected — `Issue.AddPrerequisite` and `IssueGrain.AddPrerequisiteAsync` already implement the prerequisite invariants.
  - Tests: extend `Specs/Issue/Api/IssueCreationSpecs.cs` for create-with-prerequisites success, invalid/nonexistent prerequisite, self-reference, and atomic no-partial-issue-on-failure.
- **Web** (`packages/web`):
  - New reusable issue-picker component (search + chips), consumed by `features/create-issue/ui/CreateIssueDialog.tsx` and `pages/issue-detail/ui/cards/IssueConfigurationCard.tsx`.
  - `entities/issue/api/client.ts` — extend `createIssue` to send prerequisite numbers; the backlog editor continues to use the existing `addPrerequisite`/`removePrerequisite` client behind the picker.
  - Tests: create-dialog selection/submission, backlog editor picker replacement, exclusion rules, and incomplete-prerequisite/start-eligibility messaging.
- **Runner / CLI**: none. No runner or CLI contract change.
- **Risk** (low–medium): the create endpoint gains an atomic post-creation step; validation must fail without leaving a persisted issue, and the picker must not regress the existing single-add/remove flow. Start-eligibility semantics are reused unchanged.
