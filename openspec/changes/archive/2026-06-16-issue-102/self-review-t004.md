## T-004 Self-Review — Web UI create-issue dialog frontmatter detection and risk display

### What was built

Client-side frontmatter detection in `CreateIssueDialog` (Design D4):

- **`packages/web/src/features/create-issue/lib/frontmatter.ts`** — new pure `parseIssueFrontmatter(text)` parser. Line-scanning, no YAML dependency, mirrors the CLI `FrontmatterParser` (T-002) semantics: leading `---` delimiter (BOM/CRLF tolerant), closing `---`, simple key:value with quoted values, literal (`|`) and folded (`>`) block scalars for `recommended_workflow_reason`. Returns `none | malformed | parsed`.
- **`CreateIssueDialog.tsx`** — `useMemo` over the body textarea runs the parser. When a `recommended_workflow` is present, a recommendation panel renders above a new **Workflow** selector with the reason. The selector is pre-filled from the recommendation via a `useEffect` guarded by a `workflowTouched` flag, so a manual change overrides the recommendation. A new **Risk** selector (low/medium/high, mirroring the `PRIORITIES` button pattern with `getRiskStyle`) is pre-filled from the frontmatter `risk` field, also guarded by a `riskTouched` flag. Both `workflowProfileId` and `risk` are sent through `createIssue()`.
- **`createIssue()` client** — gained `workflowProfileId?: string | null`.
- **`label-colors.ts`** — added `getRiskStyle`.

### Acceptance criteria mapping

- ✅ Body text with frontmatter shows workflow recommendation panel above workflow selector
- ✅ Workflow selector is pre-filled with recommended_workflow from frontmatter
- ✅ Recommended workflow reason is displayed alongside the recommendation
- ✅ One-click submit with recommendation creates issue with the recommended workflow profile (`CreateIssueDialog.test.tsx`: one-click submit)
- ✅ User manually changing workflow selector overrides frontmatter recommendation (`workflowTouched` guard + override test)
- ✅ Risk selector is pre-filled from frontmatter risk field
- ✅ Body text without frontmatter shows no recommendation panel
- ✅ Malformed frontmatter is silently ignored, dialog falls back to defaults
- ✅ createIssue() API client accepts and sends workflowProfileId in the request body
- ✅ Component tests: frontmatter detection, recommendation display, override behavior

### Test coverage

- `create-issue-frontmatter.test.ts` — 13 unit tests: none, parsed (full/partial), block scalars (literal/folded), quoted values, unrecognized fields ignored, malformed (colon-less / missing close), BOM, CRLF.
- `create-issue-api-client.test.ts` — added `workflowProfileId` present/omitted payload tests.
- `CreateIssueDialog.test.tsx` — 6 component tests: detection + pre-fill, reason display, risk pre-fill, no-frontmatter hides panel, malformed falls back to defaults, one-click submit, and override behavior.

### Verification

- `tsc -b --noEmit` — clean.
- `vitest run` for the 3 new/updated T-004 test files — 24/24 pass.
- Pre-existing failures in `Header`, `EpicListPage`, `live-task-cloud-event`, `useCoderSessions`, `canonical-event-types` were confirmed present on HEAD before this change (unrelated to T-004).

### Notes / decisions

- Frontmatter block is parsed but **not stripped** from the body sent to the server — the acceptance criteria and spec scenarios do not require body stripping, so behavior of the body field is preserved (minimal change).
- `recommendation` is memoized on `[frontmatter]` so the pre-fill effect is stable and idempotent (state setter bails out when the value is unchanged).
- The Workflow selector reuses the existing `useWorkflowProfiles()` data source (`WorkflowProfileInfo[]`). If the recommended id is not in the loaded profile list, it is still rendered as a selected option so the pre-fill is always visible.
