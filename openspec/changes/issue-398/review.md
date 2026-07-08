# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/web/src/shared/ui/components/{badge.tsx,button.tsx,field-error.tsx}`
  Evidence: The new semantic variants render soft-tinted backgrounds (`bg-*-subtle`) with `text-*-foreground`, and `FieldError` now uses `text-danger-foreground`. In this theme, `*-foreground` is near-white in light mode (`packages/web/src/app/styles/index.css:85,89,93,97`), which the shared status layer explicitly documents as unreadable on subtle backgrounds (`packages/web/src/shared/status-presentation/index.ts:127-133`). That makes semantic badges/buttons and inline field errors low-contrast or effectively invisible in light theme, directly violating the dark/light legibility acceptance criteria for semantic primitives. The tests codify the same wrong class choice instead of catching it (`packages/web/src/shared/ui/components/{badge,button,field-error}.test.tsx`). [disallowed:product behavior changes]
  SuggestedAction: Change soft-tinted semantic variants and `FieldError` to use readable text tokens for subtle surfaces, then update the primitive tests to assert the corrected class contract and contrast expectation.
  Verification: Render semantic `Badge`, semantic `Button`, and `FieldError` in light theme and verify readable contrast; run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/widgets/kanban-board/model/stage-colors.ts`
  Evidence: `bottomBorder` is built with a runtime template string, ``border-b-${family}-border`` (`packages/web/src/widgets/kanban-board/model/stage-colors.ts:56`). Tailwind's static extraction will not generate those classes from a dynamic string, so active kanban column bottom borders can silently fall back to the default `border-b` color in production. The current test only checks the returned string shape (`packages/web/src/widgets/kanban-board/model/stage-colors.test.ts:56-59`) and does not verify that CSS is actually emitted. This breaks the stage-accent acceptance criterion on a core board surface.
  SuggestedAction: Replace the dynamic template with an explicit per-family class map so Tailwind can see every `border-b-*-border` token at build time, and add a rendered assertion on the active column styling if possible.
  Verification: Build or run the web app and inspect active kanban column headers for `InProgress`, `Done`, and `Cancelled` to confirm the bottom border color changes from the default border; run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: `packages/web/src/shared/status-presentation/contrast.spec.ts`, `packages/web/src/widgets/kanban-board/ui/StatusPill.contrast.test.ts`
  Evidence: The specs explicitly require WCAG AA >= 4.5:1 for covered status text in both themes, but both new contrast suites intentionally relax light-theme `warning` and `muted` to 3:1 (`packages/web/src/shared/status-presentation/contrast.spec.ts:62-79,160-172` and `packages/web/src/widgets/kanban-board/ui/StatusPill.contrast.test.ts:45-52`). That means the implementation knowingly fails the documented acceptance criteria while the tests still pass. This is not just a test gap; it masks a spec violation on status pills and badges across covered surfaces. [disallowed:product behavior changes]
  SuggestedAction: Either adjust the token values/treatments until the rendered warning and muted combinations meet 4.5:1, or update the spec through the normal product/design path before merging. The review candidate cannot pass while tests assert a weaker threshold than the issue requires.
  Verification: Recompute contrast from the rendered treatment fixture for all covered states and confirm every case is >= 4.5:1 in light and dark themes.
  Status: open

## Follow-up Items

- [ID: item-4]
  Severity: follow-up
  Scope: `packages/web/src/widgets/dashboard-pulse/ui/CompactSessionCard.tsx`
  Evidence: The local stage-identity palette is dark-aware and hex-free, but it still uses raw Tailwind palette classes rather than a shared documented registry. This is acceptable for this issue's stated design, yet it leaves another color registry to keep in sync if additional stage-identity consumers appear.
  SuggestedAction: Extract the stage-identity palette into a shared registry once a second consumer appears.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-5]
  Severity: info
  Scope: non-covered web surfaces outside this milestone
  Evidence: There are still many raw palette classes and light-only combinations elsewhere in the web app, for example `widgets/coder-session/ui/SessionTimeline.tsx`, `widgets/issue-event-timeline/ui/ActivityDialog.tsx`, and several session transcript surfaces. These are outside the reviewed candidate's covered scope unless they are explicitly part of the changed surfaces.
  SuggestedAction: Address them in later UI consistency issues rather than treating them as blockers for issue 398.
  Status: out-of-scope

<promise>FAIL</promise>
