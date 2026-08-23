# Review — issue-592

## Verdict

**PASS**

## Re-review: previous finding disposition

- **MF-1 from the previous review — fixed properly.** The unrelated server test and the three server architecture-ledger files are no longer in the change. Comparing `origin/master...HEAD` shows product changes only under `packages/web` (plus the issue-592 workflow artifacts); no server, runner, or CLI file remains in the change.
- No prior must-fix finding remains unaddressed, and the repair did not introduce a regression meeting the must-fix bar.

## Must-fix findings

None.

## Review sweep

- **Acceptance criteria — checked, no issue:** the dead coder-session presentation chain and legacy session view family are deleted; the unified session hook is the session-detail contract and sole detail data path; duplicate session clients and orphaned types are removed; generic followup/turn-control and recovery paths remain; the toast host, retired markdown component, disabled MarkdownReader affordances, and duplicate viewport hooks are removed; the query-client convention is documented.
- **Coverage — checked, no issue:** the changed files cover all task groups T-001 through T-007. Public APIs were pruned, deleted-only tests were removed or narrowed, and the surviving transcript/timeline chain remains present.
- **Correctness — checked, no issue:** `useUnifiedSessionDataSource` now exports `ReturnType<typeof useUnifiedSessionDataSource>`; the shell consumes that alias and no longer reads the constant-empty sibling/title fields. Followups still use the generic mutation with stable retry idempotency keys, and the stop handle is null unless the current turn is queued or executing. The viewport replacements preserve the original media-query boundaries, and MarkdownReader retains heading-level remapping, attachments, tables, and collapsible rendering.
- **Consistency — checked, no issue:** FSD boundary and test-boundary checks pass. The remaining slice public APIs and `@x` boundary exports are consistent with the surviving consumers. The `SessionTranscriptViewMode` rename is behavior-neutral and only avoids colliding with the removed exact token.
- **Tests/build — checked, no issue:** `npm run typecheck -w packages/web`, `npm run check:fsd -w packages/web`, `npm run check:test-boundaries -w packages/web`, and `npm run test:run -w packages/web` pass. Vitest reports **355 files and 4,483 tests passed**. `npm run build -w packages/web` passes; only the documented existing Rollup warnings remain.

## Observations

- Two specialized, pre-existing components still import `react-markdown`: `packages/web/src/widgets/session-transcript/ui/TranscriptMarkdown.tsx` and `packages/web/src/widgets/issue-workflow/ui/ReviewReportModal.tsx`. The plan's broad phrase “MarkdownReader is web's only markdown renderer” could be read to include them, but the executable T-005/T-006 criteria target the unused `shared/ui/components/markdown-content.tsx` and never-enabled MarkdownReader affordances, while the plan explicitly keeps the surviving transcript chain untouched. Treat this as follow-up clarification or migration work, not a must-fix for this scoped deletion change.
- The production build continues to emit existing SignalR `/*#__PURE__*/` annotation and large-chunk warnings; neither affects issue-592's acceptance criteria.

<promise>PASS</promise>
