# Self Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: consistency
  Evidence: The proposal (`What Changes`) and the spec requirement "Settings folding and dialog state is exposed to assistive technology" both asserted that the **Template Editor** is a modal dialog requiring `aria-modal` + focus trap. The design (Decision 7, Open Question 5) and the actual code (`packages/web/src/pages/settings/ui/TemplateEditor.tsx:204`) establish that `TemplateEditor` is an inline `CardSection` panel, **not** a modal `Dialog`. The scenario "Template Editor dialog traps focus and is labelled" was therefore unachievable/false as written.
  Verification: Edited `proposal.md` line 10 to read "focus trap + `aria-labelledby` on any modal dialog (the Template Editor is currently an inline panel, not a modal — confirmed by audit)"; rewrote the spec requirement body to make the modal clause generic/conditional and added an explicit note that the Template Editor is inline; renamed the scenario to "Any modal dialog traps focus and is labelled" with a generic WHEN/THEN. Confirmed via grep that no "Template Editor dialog" assertion remains in either file, and the spec still has 11 requirements / 24 scenarios with intact `####` scenario headings.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: consistency
  Evidence: The new spec requirement "Settings heading hierarchy is monotone" requires a page-level `<h1>` and that the surface pass axe-core `heading-order`. The existing `settings-visual-consistency` capability pins the `SettingsSection` title to `<h3>`. Adding `<h1>` creates an `h1 → h3` jump that axe `heading-order` flags, which may force demoting `SettingsSection` to `<h2>` — i.e. a MODIFIED delta on `settings-visual-consistency`, contradicting this change's proposal stance of "Modified Capabilities: None".
  SuggestedAction: Defer to the T-001 baseline audit verdict. If the audit mandates `h3 → h2`, reopen the proposal's "Modified Capabilities: None" and add a `## MODIFIED Requirements` delta to `settings-visual-consistency` before archive. This is already captured in `design.md` Open Question 1 and in T-004's acceptance criteria + notes, so it is surfaced rather than hidden.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: feasibility
  Evidence: The plan's contrast verification (T-001 / T-005) depends on introducing Playwright + `@axe-core/playwright`, because jsdom cannot compute CSS `color-contrast`. The repo currently has zero e2e/Playwright infrastructure. This is the design's central technical decision (Decision 2) and is the only genuinely uncertain feasibility point.
  SuggestedAction: Obtain team sign-off on adding Playwright as a `devDependency` with a scoped `test:a11y` script before T-001 starts. If rejected, fall back to the documented alternative (a token-static guard script mapping Tailwind tokens → hex and computing ratios), accepting that context-dependent contrast (e.g. `text-muted-foreground` on `bg-muted/40`) would then need manual verification.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: feasibility
  Evidence: Associating the orphan `<label>` elements in `AiSettingsSection` with `ModelSelect` (T-004) requires `ModelSelect` to accept and forward an `aria-labelledby` prop. Whether this counts as the allowed "minimal patch" or a shared-component change (excluded by the issue Non-Goals) is a boundary judgment.
  SuggestedAction: Confirm during T-004 that `aria-labelledby` prop forwarding is treated as additive (in-scope); otherwise implement a Settings-local wrapper component that owns the `aria-labelledby` association. Recorded in `design.md` Open Question 3.
  Status: follow-up

## Review Notes

- **Alignment**: Every "What Changes" entry in the proposal traces to at least one spec requirement; all 11 spec requirements map onto the 5 tasks. All five issue acceptance-criteria groups (touch targets, keyboard navigation, contrast, screen reader, regression) are covered by spec requirements.
- **Completeness**: The single new capability `settings-accessibility` has a spec file; every requirement has ≥1 scenario; tasks carry their own tests (no separate test tasks).
- **Consistency**: Tasks reference correct spec anchors (all 5 resolve to real requirement headers); design decisions map to task notes; naming is consistent across artifacts.
- **Feasibility / granularity**: 5 tasks are functional-module slices (harness+audit, touch targets, keyboard/ARIA, heading+labels, contrast+live-regions) — none are over-fine "define interface / register DI / move file / standalone test" tasks.
- **Dependencies**: T-001 has no deps; T-002–T-005 each depend only on T-001. Verified DAG (0 bad deps), all `dependsOn` reference strictly lower-priority IDs, no cycles.

<promise>PASS</promise>
