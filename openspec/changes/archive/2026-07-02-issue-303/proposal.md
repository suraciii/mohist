## Why

Settings interactions are not trustworthy today: save/reset failures are swallowed by empty `catch {}` blocks so the user never learns a save failed; switching sections silently discards a dirty form; destructive actions use an inconsistent mix of a hand-written modal and `window.confirm`, none of which are accessible; and displayed metadata is wrong (System's Log Path is hardcoded `~/.mohist/logs/` while the adjacent Paths card reads `systemInfo.paths.logs`). This polish must land now because the Settings IA refactor (issue-302) just stabilized the shell, making these cross-section interaction fixes safe to apply on a stable surface.

## What Changes

- **Add a shared `AlertDialog` primitive** on top of the existing `dialog.tsx`, satisfying focus trap, focus restore on close, and Escape dismissal.
- **Unify all destructive confirmations** onto `AlertDialog`: Agent reset (replacing the hand-written `fixed inset-0` modal), label-definition delete, repository remove, template delete, and the `IssueDetailPage.tsx:736` comment delete (replacing `window.confirm`).
- **Surface settings save/reset failures**: replace the silent `catch {}` in `AgentSettingsSection` `handleSave`/`confirmReset` with inline error feedback (critical errors not toast-only).
- **Wire field errors to a11y**: associate Agent and Label-catalog field errors to their inputs via `aria-describedby` + `aria-invalid`, matching the existing `PreferencesSection` pattern.
- **Guard unsaved changes**: warn before switching Settings tabs when the active section's form is dirty (starting with `AgentSettingsSection`).
- **Fix System tab accuracy**: read Log Path from `systemInfo.paths.logs`; relocate the orphan amber "edit config.jsonc" banner into its card or convert to an info tooltip.
- **Typography baseline**: apply `text-balance` to section headings, `text-pretty` to descriptions, and `tabular-nums` to System/Agent numeric and mono data rows. No new motion or gradients.
- **Empty states with a next step**: replace the bare "No project selected" line in Repositories / Label catalog / Templates / Workflows with a CTA to select or create a project; give Label-catalog and Templates empty-list states an inline next-step action.
- **Label-catalog search**: add a search/filter input mirroring the Templates tab's search for consistency.

## Capabilities

### New Capabilities

- `destructive-confirmation`: A shared accessible `AlertDialog` primitive (focus trap, focus restore on close, Escape dismissal) and the rule that every destructive operation across the app — Agent reset, label-definition delete, repository remove, template delete, and comment delete — confirms through it, eliminating hand-written modals and `window.confirm`.
- `settings-form-reliability`: Settings forms are trustworthy: save/reset failures surface inline to the user (no silent swallowing, critical errors not toast-only); field errors are exposed to assistive tech via `aria-describedby` + `aria-invalid`; and a dirty form warns the user before a Settings tab switch discards unsaved changes.
- `settings-content-consistency`: Settings content is accurate and consistent: System tab metadata comes from real values (`systemInfo.paths.logs`, not hardcoded paths) with the orphan amber banner relocated; "no project" and empty-list states carry an explicit next-step CTA; Label catalog has a search input on par with Templates; and section headings/descriptions/data rows follow the typography baseline (`text-balance` / `text-pretty` / `tabular-nums`).

### Modified Capabilities

None. The `settings-shell` requirements (routing scope, grouped sub-navigation, deep-link redirect, reachability) describe the shell layout and are preserved unchanged; `web-ui` requirements describing individual section content (e.g. Workflows tab stages, workflow-profile selection) are also preserved. This change adds new behavior on top of that stable shell.

## Impact

- **Web** (`packages/web`):
  - `shared/ui/components/dialog.tsx` — add `AlertDialog` primitive (built on base-ui Dialog).
  - `pages/settings/ui/AgentSettingsSection.tsx` — replace hand-written reset modal with `AlertDialog`; fix empty `catch {}` in `handleSave`/`confirmReset` to set inline `saveError`; wire `InputField` errors to `aria-describedby`/`aria-invalid`; add dirty-guard before tab switch; apply typography baseline.
  - `pages/settings/ui/SystemSettingsSection.tsx` — read Log Path from `systemInfo.paths.logs`; relocate orphan amber banner.
  - `pages/settings/ui/LabelCatalogSection.tsx` — add search input; add field-error a11y; add empty-list CTA; route deletes through `AlertDialog`.
  - `pages/settings/ui/TemplatesSection.tsx`, `RepositoriesSection.tsx`, `WorkflowProfilesSection.tsx` — route deletes/removes through `AlertDialog`; replace "No project selected" with a CTA empty state; add empty-list next-step actions.
  - `pages/issue-detail/ui/IssueDetailPage.tsx:736` — replace `window.confirm` comment-delete with `AlertDialog`.
  - `pages/settings/ui/SettingsSection.tsx` / shared heading/description components — apply `text-balance`/`text-pretty`.
  - Tests: add coverage for `AlertDialog` (focus trap/restore/Escape), confirmation flows replacing `window.confirm`, error surfacing on save failure, unsaved-guard, and new empty states.
- **Server / runner / CLI**: none. No HTTP API, domain, or persistence change — this is a Web-only reliability and consistency polish.
- **Risk** (medium): the unsaved-guard and unified-confirmation changes alter cross-section interaction patterns touched by many sections, but introduce no data-model change; the `AlertDialog` primitive is foundational and landed first so the rest depend on it.
