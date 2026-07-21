# Self-Review — Issue 457 (issue detail consistency and polish batch)

Reviewer verdict: the plan is mostly sound (specs are well-formed, the task graph is a valid DAG, and most design decisions check out against the code), but the **theme-tokens** capability has target-identification and scope problems that must be resolved before building. Findings below cite code evidence.

## What checks out

- **Specs format** — all seven `specs/*/spec.md` use `### Requirement:` + `#### Scenario:` correctly; every requirement has ≥1 scenario (verified).
- **Tasks graph** — `tasks.json` is valid JSON, acyclic, and every `dependsOn` points to a strictly lower priority; same-file edit overlaps (BranchBar, WorkflowSessionsPanel, IssueDetailPage) are correctly sequenced via dependencies.
- **Copy (D1/D2)** — `BranchBar.tsx:129` (`未能检查上游`) and `PrDeliveryIndicator.tsx:37` (`经由 PR #N 合并`) are real; `usageText` at `WorkflowSessionsPanel.tsx:59-67` does return `'No usage yet'`. Sound.
- **Shared selects (D4)** — native `<select>` elements confirmed at `WorkflowSessionsPanel.tsx:202,217,234`; shared base-ui `Select` (`shared/ui/components/select.tsx`) and its small-select usage pattern exist. Sound (empty-value caveat already flagged).
- **Labels (D5)** — duplicate `<dt>Parent Issue</dt>` confirmed at `IssueDetailsCard.tsx:56` and `:71`. Sound.
- **Async states (D7)** — bare `Loading...` and `isError → NotFoundState` confirmed at `IssueDetailPage.tsx:218-228`; `ApiError` carries `status` (`shared/api/client.ts:8`), so 404-vs-transient branching is feasible; `Skeleton` primitive exists. Sound.
- **Disabled affordance (D8)** — `button.tsx` disabled treatment is only `disabled:opacity-50`; the `:disabled`-specificity rationale for the override is correct. Sound.

## Findings (must fix)

### F1 — Theme-token targets are misidentified and the scope does not match AC #2 (blocking)

The theme-tokens spec, design D3, and task T-002 enumerate `BranchBar.tsx`, `WorkflowRunStatusPill.tsx`, and `TaskLogPanel.tsx` as the blocks to tokenize. Two problems:

1. **`WorkflowRunStatusPill.tsx` is not rendered on the issue detail page.** A repo-wide search excluding tests finds only its definition (`WorkflowRunStatusPill.tsx:125`), the barrel re-export (`widgets/issue-workflow/index.ts:15`), and its own test — no production import or render site. Tokenizing it does not affect the page, and the T-002 acceptance criterion "WorkflowRunStatusPill maps running→info…" validates effectively dead code. It should be dropped as a target (or the dead code handled separately).
2. **The actual "task progress panel" is misidentified.** Issue-454 (archived) replaced the old "workflow step list and task progress panel" with a stage-aware task list. The design's guess that the progress panel = `WorkflowRunStatusPill` + `TaskLogPanel` is therefore unreliable; `TaskLogPanel` is an *execution log* (`TaskLogPanel.tsx:419` "Execution log"), not a progress panel. The plan must identify the current block the symptom refers to (likely the `StageBar`/`StepList` task list in `WorkflowView`) before scoping the tokenization.
3. **The scope is under-inclusive vs. AC #2.** AC #2 says "No literal palette utilities remain on the issue detail page," but several literal-palette blocks that *are* rendered on the page are outside T-002's file list, so the AC would not hold as written:
   - `PrDeliveryIndicator.tsx:20` (purple-50/200/800) — rendered via `PrDeliverySummary` at `IssueDetailPage.tsx:491`; T-001 edits this very file for copy but T-002 does not tokenize it.
   - `WorkflowSessionsPanel.tsx:48,51,54,56,167,175` (blue/green/red/gray-500, orange-600, red-600 status icons and failure text).
   - `CompositeParentOverview.tsx:14-16,37,145` and `IssueDetailPage.tsx:337` (blue/emerald/gray/red/violet badges).
   - `ReviewSummary.tsx:79-180` (green/red/gray) — rendered inside the approval review package.

   The issue's *Fix Shape* narrows the symptom to "branch bar and task progress panel," so following the narrow scope is defensible — but the plan must **explicitly reconcile** the narrow scope with the broad AC #2 (state which on-page blocks are intentionally out of scope and why), rather than silently omitting them. As written, a builder cannot tell whether these blocks are in or out.

### F2 — TaskLogPanel tokenization is imprecise and risks breaking the log console (blocking)

T-002's AC requires "No literal palette utilities … remain in … TaskLogPanel.tsx," and D3 says tokenize "the task/log presentation." But `TaskLogPanel.tsx` mixes two very different palettes:
- An **intentional log-console palette** — `text-slate-100/400/500/900`, `text-sky-300`, `text-violet-200/300`, `border-slate-200/300`, `focus:border-sky-500`, `bg-white` (`TaskLogPanel.tsx:377-449`) — deliberate styling for the execution-log surface.
- One **defect-relevant light tint** — the amber truncation badge `bg-amber-100 text-amber-800` (`TaskLogPanel.tsx:422`).

The plan must scope tokenization to the amber badge (and any other light-surface state tints) and **explicitly exclude** the deliberate log-console palette. The blanket AC as written either misses `slate`/`sky` (not in the enumerated amber/blue/red/green/gray scales) or, if applied literally, breaks console legibility.

### F3 — Factual error: non-existent "Actions" rail card (minor)

Design D6 and tasks T-005/T-006 reference "Details/Configuration/Actions cards," but the reference rail (`IssueDetailPage.tsx:559-648`) has no "Actions" card — the cards are Details, Workflow Profile, Base Drift Detected, Convergence, Configuration, Start Prerequisites, Readiness. Not blocking (the sticky fix applies to the rail column regardless of card names), but the references are inaccurate and should be corrected to avoid confusing the builder.

## Recommendation

Resolve F1 (correct theme-token target identification; reconcile scope with AC #2) and F2 (scope TaskLogPanel tokenization precisely) before building T-002; correct the F3 "Actions card" wording opportunistically. The other six capabilities are ready to build as specified.

<promise>FAIL</promise>
