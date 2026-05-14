## Context

Mohist already has the core pieces for issue diff reading: `GET /api/issues/:number/diff`, `GET /api/issues/:number/commits`, `GET /api/issues/:number/commits/:hash/diff`, an Issue Detail summary row, a dedicated `/issue/:number/files` page, and an existing `WorktreeStatus` model that exposes `ahead`, `behind`, and `canFastForward`.

The current problem is not lack of infrastructure but inconsistent semantics. The diff endpoint uses `git diff <base> <head>` while the commits endpoint already mixes `base..head` and `base...head`. The UI then presents these results as if they all mean "what this issue will merge", which is false when the issue branch is behind base. The design must therefore pull complexity downward by defining one shared comparison model in the backend and letting both Issue Detail and Files changed render that same model.

Constraints:

- Keep the lifecycle-aware availability states already used by diff and commits APIs.
- Preserve commit-specific diff inspection, but do not let it redefine the default meaning of Files changed.
- Reuse existing worktree-status knowledge instead of creating a second ahead/behind source.
- Stay reading-focused; no review comments, merge actions, or workflow changes.

## Goals / Non-Goals

**Goals:**

- Make issue-level file diffs use the same merge semantics as GitHub PR `Files changed`: merge-base to issue head.
- Return enough comparison metadata for the UI to explain what is being shown: `base`, `head`, `mergeBase`, `ahead`, `behind`, `comparison`.
- Keep diff summary, file list, and commits list internally consistent across Issue Detail and the dedicated files page.
- Add a first-class commits section on Issue Detail that helps users understand what work composes the issue.
- Reframe the changed-files page around continuous file reading, with advanced diff modes available as secondary controls.

**Non-Goals:**

- Adding PR review features such as comments, approvals, or merge operations.
- Persisting snapshots of diffs or commits after worktree removal.
- Changing workflow stage progression, worktree retention policy, or branch management behavior.
- Turning `/issue/:number/files` into a full IDE-style editor.

## Decisions

### D1: Introduce a shared issue comparison model in the backend

The backend will treat issue diff and issue commits as two projections of one logical comparison: "changes introduced by the issue branch since the current merge base with base". Instead of each endpoint independently deciding dot semantics, `packages/cli/src/api/issues.ts` should compute a single comparison context first, then feed that context into diff and commit collection.

The comparison context should contain:

- `base`: current project base branch name
- `head`: issue branch name
- `mergeBase`: merge-base sha between base and head
- `ahead`, `behind`, `canFastForward`: branch relationship metadata
- `comparison`: literal semantic marker such as `merge-base`

Implementation-wise, this should be a small internal helper near the issue review endpoints, not a new cross-project subsystem. Its job is to verify branch/worktree availability, resolve merge-base, query `WorktreeManager.getWorktreeStatus(...)`, and return a normalized structure or an existing unavailable response.

Why this choice:

- It removes duplicated branch-checking and dot-notation knowledge from multiple handlers.
- It ensures `diff.summary.filesChanged` and `commits.summary.filesChanged` come from the same semantic source.
- It keeps the UI simple by shipping a stable, explicit contract rather than forcing each screen to infer semantics from branch names.

**Alternatives considered:**

- Keep separate per-endpoint git commands and only switch `/diff` to three-dot. Rejected because summary metadata and UI framing would still drift, and future changes would have to remember hidden semantic rules in multiple places.
- Put comparison logic in `WorktreeManager`. Rejected for now because this behavior is specific to issue review API shaping, while `WorktreeManager` is currently a lower-level git/worktree primitive.

### D2: Define diff and commits using merge-base semantics, but keep commit-diff endpoint commit-scoped

`GET /api/issues/:number/diff` will use merge-base semantics for all issue-level file data: changed files, diffstat, and per-file patches should come from `git diff <base>...<head>` or equivalent merge-base-explicit forms.

`GET /api/issues/:number/commits` will continue to list commits reachable from head but not from base, i.e. `git log <base>..<head>`, because that is already the correct "commits to merge" set for a PR-style view. The endpoint will add the same comparison metadata as the diff endpoint so both payloads describe one shared review context.

`GET /api/issues/:number/commits/:hash/diff` remains a single-commit inspection endpoint. The UI must label it as commit-specific rather than issue-level Files changed.

Why this choice:

- Three-dot diff fixes the behind-base pollution bug for file review.
- Two-dot commit listing is still the correct commit-range expression for "commits in head not in base" and matches the user mental model of pending PR commits.
- Separating issue-level comparison semantics from commit-level inspection avoids overloading one endpoint with two meanings.

**Alternatives considered:**

- Use three-dot everywhere, including commit listing. Rejected because commit history is naturally expressed as reachable commits not in base; the important requirement is consistency of meaning, not identical shell syntax.
- Remove commit diff mode entirely. Rejected because the repo already has a useful commit inspection path and the requirement allows keeping it as a secondary mode.

### D3: Use API-owned comparison metadata rather than recomputing in the frontend

The frontend will consume comparison metadata from the diff/commits responses instead of deriving it from separate queries or branch names. `IssueDiffResponse` and `IssueCommitsResponse` should grow a shared metadata shape, likely something like:

- `comparison: 'merge-base'`
- `mergeBase: string`
- `ahead: number`
- `behind: number`
- `canFastForward: boolean`

Issue Detail and Files changed can still use `useWorktreeStatus` where existing UI behavior depends on worktree lifecycle actions, but review semantics should come from the review endpoints themselves. This avoids a shallow design where the UI stitches together one meaning from diff data and another from worktree status.

Why this choice:

- One response becomes self-describing.
- Screens can render explanatory copy without extra inference.
- Tests can assert exact review semantics from one endpoint contract.

**Alternatives considered:**

- Let the UI merge `/diff` with `/worktree-status`. Rejected because it spreads one concept across multiple endpoints and increases the chance of mismatched counts or stale interpretation.

### D4: Keep Issue Detail lightweight, but add a dedicated commits section

Issue Detail should not become another full diff viewer. It should keep a compact merge-summary card and add a separate commits section beneath it. That section should show a small list of recent issue commits with short hash, subject, relative time, and optional author attribution. Each row should navigate to the files page in commit mode or an anchored commit view, instead of expanding full diff content inline on Issue Detail.

The summary card and commits section should use the same availability semantics already used elsewhere:

- `not_started` -> "No changes yet" / "No commits yet"
- `worktree_removed` -> unavailable copy explaining worktree retention boundary
- `branch_missing` / `git_error` -> explicit unavailable state

Why this choice:

- It preserves Issue Detail as an orientation page.
- It exposes the commit history users need without duplicating the full changed-files reader.
- It reduces implementation complexity compared with embedding another expandable diff experience inside Issue Detail.

**Alternatives considered:**

- Reuse the current `ChangesPanel` tabbed files/commits block on Issue Detail. Rejected because the new target experience wants commits as a first-class summary surface, not a peer tab competing with file review.
- Expand commit diffs inline on Issue Detail. Rejected because it recreates a second reading surface and increases page weight.

### D5: Change Files changed into a continuous reading flow with secondary advanced controls

The dedicated files page will keep the directory tree and existing diff rendering components, but its default interaction model should change from "select a file to inspect" toward "read the whole change set with the tree as a navigator".

Design approach:

- The main pane renders all diff file blocks in order by default.
- The left tree remains for filtering, jumping, and understanding scope.
- The toolbar promotes only high-level reading controls: commit scope selector, file filter, and diff settings.
- Advanced modes such as raw patch, full file, search, split view, and commit-scoped reading move into a secondary settings menu or per-file controls.

This preserves existing reader investments while matching the spec direction that users should not need to select a file before seeing diffs.

Why this choice:

- It aligns with the GitHub PR reading model.
- It reduces the current shallow toolbar where every mode is a top-level peer button.
- It minimizes implementation churn by reusing existing diff parsing and panes.

**Alternatives considered:**

- Keep the current selected-file-only layout and only rename labels. Rejected because the acceptance criteria explicitly call for continuous file display and deemphasized advanced controls.
- Remove the tree entirely. Rejected because quick file navigation remains valuable, especially on large diffs.

### D6: Reuse existing unavailable states and tests, then add targeted semantic fixtures

The change should not invent a new availability model. Existing `available/reason/message` contracts remain the error-handling surface. New coverage should focus on semantic correctness:

- ahead-only branch: two-dot and three-dot happen to match
- ahead+behind branch: three-dot excludes base-only files
- real regression case like #199 or a fixture equivalent: file count drops from polluted two-dot result to correct merge-base result
- commit-mode behavior still loads a single commit diff without changing default issue-level counts

Why this choice:

- It keeps error handling stable.
- It adds tests exactly where the bug lives: git semantics and summary consistency.

**Alternatives considered:**

- Broad snapshot-style UI rewrites only. Rejected because the core risk is semantic drift, which is best caught with focused API and component assertions.

## Risks / Trade-offs

- [Backend comparison metadata diverges from `WorktreeStatus`] -> Derive `ahead`, `behind`, and `canFastForward` from `WorktreeManager.getWorktreeStatus(...)` inside the shared comparison helper instead of recomputing independently.
- [Three-dot diff summary and `git log base..head` may confuse future maintainers because the shell syntax differs] -> Encode the shared semantic explicitly as `comparison: merge-base` and document in code comments that diff and commit listing use different git commands to represent the same user-facing merge contract.
- [Continuous rendering of all file diffs may hurt performance on large changes] -> Keep the existing large-diff guardrails, preserve collapsible sections, and avoid eager rendering for oversized file patches.
- [Issue Detail commits section may duplicate information already visible on the files page] -> Keep it intentionally shallow: summary rows and navigation only, no full inline diff viewer.
- [UI migration could cause temporary inconsistency if Issue Detail and Files page switch at different times] -> Land backend contract first, then update both screens together behind the new response shape in the same change.

## Migration Plan

1. Update spec deltas for `issue-changed-files-reader`, `issue-review-surface`, `http-api`, and `web-ui` to codify merge-base semantics and Issue Detail commits behavior.
2. Refactor `packages/cli/src/api/issues.ts` to introduce a shared issue-comparison helper and update diff/commits payload shapes.
3. Extend frontend types and queries to consume the new metadata without changing availability-state handling.
4. Update `IssueDetailPage.tsx` to render merge framing and a lightweight commits section.
5. Update `IssueChangedFilesPage.tsx` to use continuous diff rendering, PR-style header copy, behind-base notice, and secondary advanced controls.
6. Add focused backend and frontend tests for ahead-only, ahead+behind, unavailable states, and commit navigation behavior.
7. Rollback strategy: revert the API/helper changes and UI copy/layout changes together. No database migration or persisted data format is involved.

## Open Questions

- Should the files page commit navigation deep-link via query params such as `?commit=<sha>` so Issue Detail commit rows can link directly into commit mode, or is in-page state only sufficient for this change?
- Should Issue Detail show only the latest N commits with a `View all commits` link, and if so what fixed N best matches the current layout without causing scroll bloat?
