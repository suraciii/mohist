## Context

The Settings surface (`packages/web/src/pages/settings/ui/*`, 6 tabs) grew incrementally without a shared visual contract. Today each tab hand-rolls its own card wrappers (`rounded-md`/`rounded-lg` + `border` + `bg-muted`/`bg-card/50`), its own page title (`<h3 className="text-sm font-medium text-foreground">`), and a sprawl of text-color tokens (`text-gray-*`, `text-foreground/85`/`/80`/`/75`). Only `SystemSettingsSection` uses the existing `CardSection` component (`packages/web/src/shared/ui/components/card-section.tsx`). `ModelSelect.tsx` defines three inline SVG icon components while the rest of the app uses `lucide-react`.

Constraints:
- Pure visual refactor — **no** API, data-model, or behavior change (see spec: "preserves existing behavior").
- `CardSection` is a shared component also consumed by `pages/issue-detail/ui/IssueDetailPage.tsx` (8 call sites), so changes to it have blast radius outside Settings.
- This issue is the visual counterpart to functional work in #19 / Issue C; it must not touch functional behavior they own.

Stakeholders: Settings end-users (visual consistency, accessibility), and any future Settings contributor (the contract this design establishes).

## Goals / Non-Goals

**Goals:**
- One card component (`CardSection`) for all Settings section/row cards.
- One page-title wrapper (`SettingsSection`) rendering a single `<h3>` style across all 6 tabs.
- Text color converges to three tiers: `text-foreground`, `text-muted-foreground`, `text-foreground/70`.
- All Settings icons come from `lucide-react`; no inline `<svg>` (except inside shadcn primitives).
- Card titles are visually uniform across tabs (System tab no longer jumps).
- Grep-enforceable acceptance (0 hits for the banned tokens/patterns under `pages/settings`).

**Non-Goals:**
- No new Settings features, no copy/wording changes, no layout reflow beyond swapping wrappers/classes.
- No backend, API, persistence, or SSE changes.
- Not converging **form-control** rounding (`Input`/`Textarea` with `rounded-lg border border-input`) — those are shadcn-style inputs, not cards.
- Not refactoring `IssueDetailPage`'s usage of `CardSection` beyond what the shared-component palette fix requires.
- Not introducing a full design-system token layer beyond the three text tiers.

## Decisions

### D1: `SettingsSection` page wrapper — new, settings-local
Add `pages/settings/ui/SettingsSection.tsx` (settings-scoped, not in `shared/ui`). Props: `{ title: string; description?: string; children: ReactNode }`. It renders exactly:
```
<h3 className="text-sm font-medium text-foreground">{title}</h3>
{description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
<div className="mt-4 space-y-4">{children}</div>
```
This captures the style every tab currently duplicates. Each of the 6 section components wraps its top-level return in `<SettingsSection title="...">` and deletes its local `<h3>`.
- *Alternative considered:* generalize into `shared/ui`. Rejected — page-title semantics are Settings-specific; keep it local until a second consumer appears.

### D2: `CardSection` is the sole section/row card; fix its palette violation
Migrate `RepositoriesSection` repo cards, `TemplatesSection`/`TemplateRow`, `TemplateEditor` outer card, `WorkflowProfilesSection` profile accordion + YAML box, `AiSettingsSection`, and `AgentSettingsSection`'s inner box to `CardSection`. Critically, **`CardSection` itself violates the new palette** today: its default tone title class is `text-foreground/80` (`card-section.tsx`). Remap that to `text-muted-foreground`. No other structural change to `CardSection`.
- `titleAs` stays configurable; **default `h2` is left unchanged** so `IssueDetailPage` (which relies on it) is unaffected. Settings call sites MAY pass `titleAs="h4"` for outline correctness behind the page `h3`, but visual consistency is driven by classes, not tags.
- *Alternative considered:* change default `titleAs` to `h4`. Rejected — larger blast radius (IssueDetailPage), purely an outline concern, invisible visually.

### D3: Sub-element info boxes are NOT cards
Small inner panels (`rounded-md border px-3 py-2 text-xs text-muted-foreground`, e.g. System "Update log" box, WorkflowProfiles stage box) are sub-elements, not section cards. They stay as-is (and already use allowed tokens). The acceptance grep for `rounded-md`/`rounded-lg` mixing targets **card containers**, not these panels or form controls.

### D4: Token remapping table (explicit, per site)
To make the collapse deterministic and reviewable:

| Current | Maps to | Typical site |
|---|---|---|
| `text-gray-500` | `text-muted-foreground` | RepositoriesSection |
| `text-foreground/85` | `text-foreground` (body) or `text-muted-foreground` (caption) | WorkflowProfiles YAML preview, descriptions |
| `text-foreground/80` (form labels, CardSection title) | `text-muted-foreground` | AgentSettingsSection labels, TemplateEditor labels, CardSection |
| `text-foreground/75` | `text-muted-foreground` | (audit + remap) |
| `text-foreground/70` | **keep** (allowed weakest tier) | existing emphasis |

Rule of thumb: form labels and card titles → `text-muted-foreground`; primary readable body text that was near-full-opacity → `text-foreground`.
- *Alternative considered:* keep `text-foreground/80` as a fourth tier. Rejected — the issue explicitly bans it and flags WCAG risk.

### D5: `ModelSelect` inline SVG → `lucide-react`, 1:1
Delete the three local components (`SearchIcon`, `ChevronDownIcon`, `XIcon`, lines 33-55). `import { Search, ChevronDown, X } from 'lucide-react'`. Swap the three usage sites (lines 216, 222, 283). Lucide icons accept the same `className` (incl. the `isCompact` conditional sizing), so no layout change. No other inline `<svg>` exists under `pages/settings` (verified).

### D6: `SectionState` (loading/error/empty) aligns to the card style
`SectionState.tsx`'s loading skeleton and empty state currently mix `rounded-lg border bg-card/50` and `rounded-md border-dashed`. Normalize: the loaded-content skeleton mirrors `CardSection` (`rounded-lg border bg-card/50`); the empty state keeps `rounded-md border-dashed` (it is a sub-element, per D3).

### D7: Regression guard via a grep test
Add a small vitest spec (e.g. `settings-consistency.test.ts`) that reads the settings source files and asserts 0 matches for `text-gray-`, `text-foreground/8`, `/75`, `/80`, and `<svg` (outside shadcn). This turns the one-time acceptance grep into a permanent contract matching the new spec requirement.

## Risks / Trade-offs

- **[Over-applying the `rounded-*` ban to form controls]** → Mitigation: D3/D6 explicitly scope the rule to card containers; the regression test (D7) targets card wrapper patterns, not `border-input` form controls.
- **[Token collapse reduces hierarchy granularity (4 opacity tiers → 2)]** → Mitigation: lean on `font-weight`/`font-size` (already in use) for hierarchy instead of opacity. Acceptable: the three tiers still express primary/secondary/weakest.
- **[`CardSection` shared with `IssueDetailPage`]** → Mitigation: only the palette class on the title changes (`/80` → `text-muted-foreground`); no structural/default-tag change. Visually near-identical (muted-foreground ≈ foreground at 80% on muted bg). Verify with the issue-detail screenshot in the Before/After set.
- **[Visual regression across 6 tabs]** → Mitigation: Playwright Before/After screenshots per tab + axe-core contrast pass on Settings body text, as acceptance.
- **[Per-site token judgement is subjective]** → Mitigation: D4 table fixes the mapping; review can diff against the table.

## Migration Plan

Frontend-only, single PR, no feature flag, no rollout stages.

1. Fix `CardSection` default title token (`/80` → `text-muted-foreground`) and add `SettingsSection`.
2. Migrate the 6 section components + `SectionState` + `TemplateEditor` onto `CardSection` / `SettingsSection`.
3. Apply the D4 token remap across all settings files (grep-driven, file by file).
4. Swap `ModelSelect` inline icons to `lucide-react`.
5. Add the D7 grep regression test.
6. Verify: `SettingsPage.test.tsx`, `AiSettingsSection.test.tsx`, `ModelSelect.test.tsx` pass; run the new consistency test; capture Before/After screenshots; run axe-core on Settings.

**Rollback:** Revert the single commit/PR. No data migration, no flags to flip. The `SettingsSection` file and D7 test are additive and revert cleanly.

## Open Questions

- Should sub-element info boxes (D3) eventually become a named `<InfoBox>` component? Deferred — out of scope here; file as follow-up tech debt.
- Do we want the D7 grep test to also enforce the `rounded-md`/`rounded-lg` card rule structurally (vs. just banning tokens)? Lean: start with token + `<svg>` bans (unambiguous); card-radius rule stays manual review + screenshot diff for now.
