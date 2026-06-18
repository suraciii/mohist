## Why

The Settings page's 6 tabs (`/audit-test-1/settings/*`) were built incrementally with no shared visual contract, producing visible inconsistency: Card wrappers mix `rounded-md`/`rounded-lg` and ad-hoc border/background classes; text color drifts across `text-gray-*` hardcoded values and arbitrary `text-foreground/85`/`/80`/`/75` opacities (a WCAG AA contrast risk); `ModelSelect` inlines hand-written SVG icons while the rest of the app uses `lucide-react`; and page titles jump between `h3` and `h2` styles across tabs. This makes the surface look unfinished, hurts accessibility, and forces every new section to re-decide the same styling. With functional fixes (#19, Issue C) landing, now is the moment to lock the visual contract before more sections copy the inconsistencies.

## What Changes

- **Unify Card component**: Lock `CardSection` (`shared/ui/components/card-section.tsx`) as the only section-card wrapper across all 6 tabs. `RepositoriesSection`, `TemplatesSection`/`TemplateRow`, `WorkflowProfilesSection`, `AiSettingsSection`, and `AgentSettingsSection` replace their hand-rolled `rounded-md/lg + border` wrappers with `CardSection`. No more `rounded-md` vs `rounded-lg` mixing.
- **Introduce `<SettingsSection>`**: Add a page-level wrapper (`title` + optional `description`) that renders the page title as a consistent `<h3>`, replacing the per-file `h3 className="text-sm font-medium"` duplication. Card titles remain internal to `CardSection` (already `titleAs`-aware: `h2`/`h3`/`h4`).
- **Converge color tokens to three tiers**: `text-foreground` (primary), `text-muted-foreground` (secondary), `text-foreground/70` (weakest emphasis only). Delete every `text-gray-*` and every `text-foreground/85` / `/80` / `/75` under `pages/settings`. Map current usages to the nearest allowed tier.
- **Replace inline SVG with `lucide-react`**: Remove the hand-rolled `SearchIcon`/`ChevronDownIcon`/`XIcon` in `ModelSelect.tsx` (lines 33-55) in favor of the `lucide-react` icons used elsewhere. Audit other Settings files for stray `<svg>` and convert them too.
- **Standardize heading hierarchy**: Page titles → `<SettingsSection>`'s single `<h3>` style. Card titles → the fixed `CardSection` title style (uppercase, tracked, `titleAs` configurable). Eliminate the System-tab `h2 uppercase tracking-wider` jump.

## Capabilities

### New Capabilities

- `settings-visual-consistency`: The visual design contract governing the Settings surface — the single Card component (`CardSection`) and page wrapper (`SettingsSection`), the allowed color-token palette (three tiers, no hardcoded grays, no ad-hoc opacities), the sole icon source (`lucide-react`), and the heading hierarchy for page vs card titles. Each Settings section MUST conform; deviations are detectable by grep.

### Modified Capabilities

<!-- None. This is a pure visual refactor: no user-visible behavior, API, or data-model change. Functional behavior governed by `settings-system-diagnostics` (log level, diagnostics) and `web-ui` (SSE, approvals) is intentionally untouched. -->

## Impact

- **Shared components**: `CardSection` (`packages/web/src/shared/ui/components/card-section.tsx`) becomes the load-bearing card; verify its `titleAs` and tone API cover the migrated sections (may need a `description` slot). New `SettingsSection` wrapper added under `pages/settings/ui` (or `shared/ui`).
- **Settings section files**: `AiSettingsSection`, `AgentSettingsSection`, `RepositoriesSection`, `TemplatesSection`, `TemplateEditor`, `NewTemplateDialog`, `WorkflowProfilesSection`, `SectionState`, `SystemSettingsSection` — wrapper, token, and (where present) SVG/icon edits.
- **`ModelSelect.tsx`**: Drop three inline icon components, import `Search`, `ChevronDown`, `X` from `lucide-react`.
- **No backend / API / data-model changes**; no new HTTP routes or persistence.
- **Tests / verification**: `SettingsPage.test.tsx`, `AiSettingsSection.test.tsx`, `ModelSelect.test.tsx` must pass. Acceptance is grep-based (0 hits for `text-gray-`, `text-foreground/8`, `/75`, `/80`, and `<svg` under `pages/settings` except inside shadcn primitives) plus a Before/After visual diff and an axe-core contrast pass on Settings body text.
