/**
 * Public API of the settings-search feature.
 *
 * The settings-search feature owns the registry of searchable settings
 * descriptors, the keyboard-shortcut catalogue consumed by the Preferences
 * tab, the keyboard-shortcut handler registry, and the cmdk-based settings
 * search dialog. This file is the single import surface for downstream code.
 */
export { settingsSearchRegistry } from './registry'
export {
  SHORTCUTS,
  registerShortcutHandler,
  getShortcutHandler,
  __resetShortcutHandlersForTesting,
} from './keyboard-shortcuts'
export type { ShortcutDefinition, ShortcutId } from './keyboard-shortcuts'
export type { SettingsSearchEntry, SettingsTab } from './types'
export { SettingsSearch } from './SettingsSearch'
