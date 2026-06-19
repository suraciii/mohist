/**
 * Central settings-search registry.
 *
 * Aggregates a flat list of searchable settings descriptors exported by each
 * Settings section component. Each entry describes one configurable field:
 * the tab it belongs to, its label, a human description, optional
 * placeholder text, and a stable `focusTargetId` that resolves via
 * `document.getElementById` to a focusable element on that tab.
 *
 * The descriptor arrays are owned by their respective section modules so the
 * registry is a pure aggregation: a section declares its own fields and the
 * registry simply collects them at module load. There is no runtime
 * registration — descriptors are static metadata independent of server data.
 *
 * Consumers (e.g. the settings search dialog) read `settingsSearchRegistry`
 * to filter, render results, and focus the target element via
 * `document.getElementById(entry.focusTargetId)?.focus()`.
 */

export type SettingsTab =
  | 'ai'
  | 'agent'
  | 'repositories'
  | 'workflows'
  | 'templates'
  | 'system'
  | 'preferences'

export interface SettingsSearchEntry {
  /** Owning tab. Used both for grouping and for navigation on Enter. */
  tab: SettingsTab
  /** Human-readable label rendered as the primary result line. */
  label: string
  /** Short description rendered as the secondary line. */
  description: string
  /** Optional placeholder text; included in the searchable haystack. */
  placeholder?: string
  /**
   * Stable id of the field's focusable element. Must resolve via
   * `document.getElementById` to a focusable element on its tab.
   */
  focusTargetId: string
  /**
   * Optional DOM event dispatched after navigating to the owning tab and
   * before polling the focus target. Sections use this to reveal conditional
   * controls, such as collapsed advanced fields, so the target can mount.
   */
  revealEvent?: string
}
