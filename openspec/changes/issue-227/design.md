## Context

After creating an issue, users get no reliable confirmation of which issue was just created. The observed toast renders `undefined` in place of the issue number.

Verifiable current state in `packages/web`:

- `createIssue` (`entities/issue/api/client.ts:21`) returns `request<Issue>(...)`. The resolved value is a **bare `Issue`** whose number lives at `Issue.number` (`model/issue.ts:83`, a required `number`).
- The sibling mutation helpers — `startIssue`, `closeIssue`, `reopenIssue` (`client.ts:51-60`) — return a **wrapper** `{ issue: Issue; message: string }`.
- The create `useMutation` in `CreateIssueDialog.tsx:199` declares `onSuccess: () => { ... }` — it discards the API response entirely, so it cannot render the number even if a toast were added naively.

Root cause of the `undefined`: the create response is a bare `Issue`, but the number was read through a `.issue` wrapper-shaped path (the convention used by start/close). `data.issue` is `undefined` on the bare create response, so `data.issue.number` (or optional-chained variants) renders `undefined`. The backend returns the correct number; this is purely a Web response-shape mismatch.

The codebase already standardizes on `sonner` (`import { toast } from 'sonner'`) for success/error toasts — used by `entities/epic`, `entities/settings`, `entities/label-catalog`, `entities/project`, and `LiveTaskProvider`.

## Goals / Non-Goals

**Goals:**

- Surface the newly created issue's `number` in a success toast: `Issue #223 created`.
- Capture the create API response in the mutation `onSuccess` and read `number` from `Issue.number` (the bare response), not from a `{ issue }` wrapper.
- Surface a number-free error toast on a failed create.
- Add/extend tests pinning the rendered number against the bare `Issue` response shape.

**Non-Goals:**

- No API contract change to `POST /issues` (it already returns the full `Issue` with `number`).
- No new toast animation or interaction.
- No changes to other (start/close/reopen) mutation flows.
- No navigation/links from the toast (possible follow-up).

## Decisions

### Decision 1 — Read the number from the bare `Issue` response, not a wrapper

`createIssue` resolves to a bare `Issue`; the number is `data.number`. The `{ issue, message }` wrapper belongs only to start/close/reopen.

- **Alternative A:** Refactor `createIssue` to return `{ issue: Issue; message: string }` for shape symmetry with the other mutation helpers. **Rejected** — ripples to every create caller and violates the explicit "no API contract change" non-goal.
- **Alternative B:** Ignore the response and refetch/derive the number from the invalidated `['issues']` list. **Rejected** — racy (invalidation is fire-and-forget) and slower; the authoritative number is already in the response.

### Decision 2 — Capture the response in `onSuccess` and emit a `sonner` toast

Change `onSuccess: () =>` to `onSuccess: (data: Issue) =>` and call `toast.success(\`Issue #${data.number} created\`)` before `queryClient.invalidateQueries` and `resetAndClose()`. This matches the established `toast.success(...)` pattern (e.g. `entities/epic/api/queries.ts:33`).

- **Alternative:** Extract a `useCreateIssueMutation()` hook in `entities/issue/api/queries.ts` to colocate toast + invalidation, mirroring the epic/settings queries. **Deferred** — today the dialog is the only create caller; a hook is a worthwhile cleanup only if more create call sites appear. Noted as an open question.

### Decision 3 — Error path emits a number-free error toast

Add `onError: (err) => toast.error(err.message || 'Failed to create issue')`. The error toast references no issue number (there is none on failure), satisfying the spec scenario and avoiding any `undefined` leakage.

- **Alternative:** Rely solely on the existing inline `mutation.error` banner (`CreateIssueDialog.tsx:413`). **Rejected** — the spec requires a toast, and the inline banner is not a toast surface.

## Risks / Trade-offs

- **[Response-shape drift]** → `request<Issue>` types `data` as `Issue`, so a future wrapper change would surface as a TypeScript error at `data.number`. Reinforce with a test that mocks `createIssue` resolving to a bare `{ number }` and asserts the literal `Issue #<n> created`.
- **[Sonner mock leakage in tests]** → Hoist a `toast` mock via `vi.hoisted` (pattern from `SettingsSearch.test.tsx:17` and `EpicListPage.test.tsx:11`) and reset in `afterEach`. Existing `CreateIssueDialog.test.tsx` mocks must be extended to cover the new toast assertions without destabilizing the template/model tests.
- **[Toast before invalidation completes]** → Cosmetic only; `invalidateQueries` is fire-and-forget and the toast carries no dependency on refreshed list data. Order between toast and invalidate is not user-visible.

## Migration Plan

- Web-only change. No backend or data migration; ships with the next frontend release.
- **Rollback:** revert the dialog + test change. Worst case restores the current behavior (no/`undefined` toast); no lasting state.

## Open Questions

- Should the success toast gain a "Open issue" action/link to the new issue? Out of scope here; candidate follow-up.
- Promote the create mutation into a `useCreateIssueMutation()` hook shared across future create call sites? Defer until a second caller appears.
