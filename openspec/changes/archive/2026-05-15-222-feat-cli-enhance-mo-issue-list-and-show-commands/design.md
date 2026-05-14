## Context

`mo issue list` currently passes a single `stage` query parameter to `GET /api/issues`, and the server accepts it without validating whether it is a real workflow stage. This makes unknown stages look like empty result sets and prevents multi-stage or alias-based triage.

`mo issue show` currently always renders the full issue detail and then fetches additional session and execution data. This is useful for deep inspection but too verbose and too expensive for quick status checks or shell composition.

`mo issue diff` currently shells out from the CLI after fetching issue details, while the server already exposes `GET /api/issues/:number/diff` with merge-base comparison metadata, per-file stats, full file diffs, and typed unavailable reasons. The new stat mode should reuse that server-owned comparison path instead of creating a second branch/worktree interpretation in the CLI.

## Goals / Non-Goals

**Goals:**

- Add a small issue-list query language for stage selection, the `active` alias, and attention filtering.
- Keep invalid stage or alias input as an explicit command/API error rather than an empty result.
- Keep filtering composition predictable: stage selection is OR internally; stage, attention, priority, label, and archive scope are AND together.
- Add compact human-readable issue show output without changing the default full output.
- Add diff stat output that shares the same base/head semantics and availability checks as full issue diff.
- Keep CLI help text aligned with the new behavior.

**Non-Goals:**

- Do not add a real workflow stage or change stored issue stage/status values.
- Do not add `--my` or any assignee/creator/reviewer identity model.
- Do not classify normal running/probing sessions as attention items.
- Do not turn `mo issue list` into a general query language beyond the requested filters.
- Do not change Web UI board filtering behavior.

## Decisions

### D1: Resolve list selection on the server

`GET /api/issues` will own parsing and validation for the new list scope inputs: comma-separated `stage`, the `active` alias, and `attention=true`. The CLI will continue to act as a thin client by forwarding user options and formatting the returned issue list or API error.

The server should normalize `stage=build,check` into a stage set, expand `active` into `[plan, build, check, integrate]`, reject unknown values with HTTP 400, and then apply filters in this order conceptually: archive scope, stage selection, attention selection, priority, label. The exact implementation can be in one local helper near the route unless the logic becomes reused elsewhere.

**Alternatives considered:** Put all parsing in `issue.ts` and send repeated API calls for each stage. That would duplicate query semantics in the CLI, make API behavior less testable, and violate the existing thin-client design. Add a new endpoint like `/issues/attention`. That would create another surface for the same list resource when a composable filter is sufficient.

### D2: Model `active` as a query alias, not data

`active` will be represented only as parser vocabulary for issue-list stage selection. It expands to pipeline stages that have started and are not terminal delivery: `plan`, `build`, `check`, and `integrate`. It does not include `backlog`, even when issue status is `active`, and it excludes closed/completed issues through the same terminal-status filtering used by the active alias predicate.

**Alternatives considered:** Add `active` to the `Stage` enum. That would blur persisted workflow state with user query language and risk accidental state transitions to a non-stage. Reuse `IssueStatus.Active`. That status includes backlog work and does not represent pipeline progress.

### D3: Define attention from existing issue state

Attention filtering will be a predicate over existing fields and delivery classification:

- `approvalState.status === 'awaiting'` for the current issue stage.
- `status` is `blocked` or `interrupted`.
- `classifyMergeDelivery(issue)` is `blocked`, `build-failed`, `conflict`, or `done-not-merged`.
- Completed/done but not merged is covered by `done-not-merged`.

Normal active issues, running sessions, and probing sessions are not attention by themselves. The initial implementation should not query coder sessions for attention because the product definition is issue decision state, not agent liveness.

**Alternatives considered:** Use active coder session status to infer attention. That would incorrectly include normal running/probing work and would require extra per-issue lookups during list. Add a new persisted `needsAttention` column. That adds synchronization risk for a derived state that can be computed from existing issue fields.

### D4: Keep compact show as a formatting mode over the existing issue detail response

`mo issue show <id> --compact` will fetch `/issues/:number` and format one line, for example `#215 build blocked p1 "title"`. It should not fetch coder sessions or executions, and it should not print body, comments, checks, approval output, branch metadata, or warnings.

The line should be intentionally human-readable rather than machine-stable JSON. If scripts need stable parsing later, that should be a separate explicit JSON mode.

**Alternatives considered:** Add a dedicated compact API endpoint. The existing detail response already contains the needed fields, and compactness is an output concern. Reuse `issue list` row formatting. List rows include columns and padding optimized for tables, not a single-line command result.

### D5: Route diff output through the server diff API

`mo issue diff <id>` should use `GET /api/issues/:number/diff` for both default and `--stat` modes. For default mode, the CLI can concatenate returned per-file `diff` values and print the full patch, preserving default behavior while aligning semantics with the merge-base API. For `--stat`, the CLI prints `summary` plus each file's additions/deletions/binary marker and does not print patch content.

Unavailable responses already distinguish `not_started`, `worktree_removed`, `branch_missing`, and `git_error`; the CLI should render those messages clearly and return a non-zero exit for unavailable diff data. If the diff is available but `summary.filesChanged === 0`, render a clear no-changes message and exit successfully.

**Alternatives considered:** Keep the CLI `git diff` shell-out and add `--stat` there. That would retain divergent two-dot vs merge-base semantics and weaker unavailable reasons. Add `?stat=true` to the server endpoint to avoid full patch generation. This can be considered later for very large diffs, but the current endpoint already computes stats and full diff together, and this change is CLI behavior rather than performance work.

### D6: Prefer small local helpers over new modules

The implementation can add focused helpers such as `parseStageSelection`, `isAttentionIssue`, `formatCompactIssue`, and `formatDiffStat` close to the command or route that uses them. Move helpers into shared modules only if both CLI and server need the same logic.

**Alternatives considered:** Create a general issue query language module. The requested behavior is narrow, and a broad abstraction would add unnecessary concepts before there is reuse pressure.

## Risks / Trade-offs

- [Risk] Attention semantics can drift from delivery-state semantics if duplicate predicates are introduced. -> Mitigation: reuse `isCurrentStageApproval` and `classifyMergeDelivery` where possible, and keep the attention predicate centralized in one server helper.
- [Risk] `active` is overloaded with `IssueStatus.Active`. -> Mitigation: name implementation helpers around stage selection or alias expansion, and never add `active` to `Stage`.
- [Risk] Moving default `mo issue diff` to the API may slightly change output ordering or headers compared with raw `git diff`. -> Mitigation: print per-file diff blocks in the server response order and keep no extra decoration in default mode.
- [Risk] Server-side filtering by loading issues then filtering in memory may be less efficient for large projects. -> Mitigation: acceptable for current local SQLite/project scale; add repo-level multi-stage predicates only if tests or profiling show need.
- [Risk] API users may rely on unknown `stage` returning an empty list. -> Mitigation: invalid scope is explicitly part of this change and should return a clear 400 error.

## Migration Plan

1. Add server-side parsing helpers for stage selections and attention filtering in the issue API route.
2. Extend `GET /api/issues` to accept comma-separated `stage` values and `attention=true`, returning 400 for invalid stage or alias names.
3. Update `mo issue list` options/help to mention comma-separated stages, `active`, and `--attention`; forward parameters and exit non-zero on API errors.
4. Add `--compact` to `mo issue show` and short-circuit before fetching sessions/executions when compact mode is used.
5. Update `mo issue diff` to call `/issues/:number/diff` for default and `--stat` modes, rendering unavailable and no-change cases distinctly.
6. Add tests for server list filtering, CLI option/help behavior where practical, compact output, and diff stat formatting.

Rollback is straightforward: remove the new CLI flags and revert `GET /api/issues` query parsing to single-stage behavior. No database migration or stored data cleanup is required.
