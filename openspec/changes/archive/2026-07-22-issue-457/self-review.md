# Self-Review — Issue 457 (issue detail consistency and polish batch) — re-review

Re-review after the fix task. The three findings from the prior review (F1 theme-token target misidentification / AC #2 scope; F2 TaskLogPanel precision; F3 "Actions card") are all resolved. The plan is ready to build, with one non-blocking nit noted below.

## Prior findings — verified resolved

- **F1 (targets + AC #2 scope):** Resolved. `WorkflowRunStatusPill.tsx` is dropped as a target everywhere (design Context #2, D3, Risks; proposal Impact; tasks T-002 description/AC/output) with the correct rationale (not rendered on the page). The "task progress panel" is correctly traced: issue-454 refactored it into the already-tokenized `StageBar`/`StepList`/`TaskItem`, leaving `TaskLogPanel.tsx` as the remaining task surface. AC #2 is explicitly reconciled (design D3 "Scope reconciliation", proposal Impact): the broad first clause is scoped by the "every listed block" second clause to the branch bar + task execution/progress log; every other on-page literal-palette block is enumerated as out-of-scope with rationale, and the broad-sweep alternative is considered and rejected on Fix-Shape grounds. A builder can tell exactly what is in and out.
- **F2 (TaskLogPanel precision):** Resolved. The theme-tokens spec adds a scenario requiring the deliberate dark console surface (`bg-slate-900` + light foreground line colors) to be preserved unchanged; design D3 scopes tokenization to the light-surface chrome + amber badge and explicitly excludes the console interior; T-002 has matching AC ("dark console preserved unchanged") and a test requirement.
- **F3 ("Actions card"):** Resolved. Design D6 now lists the actual rail cards and states there is no separate "Actions" card; T-005 AC wording corrected.

## Cross-checks that pass

- All seven capabilities map cleanly: proposal (7) ↔ specs (7 dirs) ↔ tasks (7). Tasks form a valid DAG; every `dependsOn` points to a strictly lower priority; same-file overlaps (BranchBar, WorkflowSessionsPanel, IssueDetailPage) are sequenced via dependencies.
- All specs are well-formed (`### Requirement:` + `#### Scenario:`, 4 hashtags, ≥1 scenario per requirement).
- Technical decisions re-verified against code: `ApiError.status` exists (D7); semantic token families exist in `index.css` (D3); `Skeleton` primitive exists (D7); shared base-ui `Select` + small-select pattern exist (D4); duplicate `Parent Issue` labels confirmed (D5); `button.tsx` disabled = `opacity-50` only (D8).
- Copy/selects/labels/rail/async-states/disabled capabilities are sound and unchallenged.

## Non-blocking nit (does not impede building)

- `design.md:91` (Risks, "Dark-theme contrast after token swap") still lists "status pill" among the blocks to verify in dark mode. This is a leftover phrase from before `WorkflowRunStatusPill` was dropped; it is inconsistent with D3/T-002 (which exclude the pill) but does not change scope or behavior — D3 and T-002 are unambiguous that the pill is out of scope. Suggest deleting "status pill" from that verification note in a follow-up touch-up.

## Note for the issue owner (non-blocking)

- The AC #2 reconciliation is a defensible interpretation aligned with the issue's own Fix Shape ("branch bar and task progress panel"), and the plan documents it explicitly with the alternative rejected. If the owner instead intends the literal page-wide palette sweep, that is a scope expansion to track separately — but as written the plan is internally consistent and buildable.

<promise>PASS</promise>
