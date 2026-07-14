/**
 * Central settings-search registry.
 *
 * Aggregates the descriptor arrays exported by each Settings section
 * component into a single static list. The registry is the data substrate
 * consumed by the settings search dialog (T-004) and any other code that
 * needs to enumerate searchable settings.
 *
 * Each section owns its own descriptors and exports them alongside the
 * component (e.g. `AgentSettingsSection` exports `AGENT_RUNTIME_DESCRIPTORS`).
 * This module simply imports them and concatenates them. There is no
 * runtime registration: descriptors are static metadata independent of
 * server data, which keeps the registry trivially testable and grep-able.
 *
 * Consumers read `settingsSearchRegistry` to filter, render results, and
 * focus the target element via
 * `document.getElementById(entry.focusTargetId)?.focus()`.
 */
import { AI_SETTINGS_DESCRIPTORS } from '../ui/AiSettingsSection'
import { AGENT_RUNTIME_DESCRIPTORS } from '../ui/AgentSettingsSection'
import { PREFERENCES_DESCRIPTORS } from '../ui/PreferencesSection'
import { REPOSITORIES_DESCRIPTORS } from '../ui/RepositoriesSection'
import { SYSTEM_DESCRIPTORS } from '../ui/SystemSettingsSection'
import { TEMPLATES_DESCRIPTORS } from '../ui/TemplatesSection'
import { WORKFLOW_DESCRIPTORS } from '../ui/WorkflowProfilesSection'
import type { SettingsSearchEntry } from './settings-search'

export type { SettingsSearchEntry, SettingsTab } from './settings-search'

/**
 * The flat list of every configurable field across all Settings tabs.
 * Order matches the in-page order of fields; consumers should not rely on
 * it for behaviour beyond display grouping.
 */
export const settingsSearchRegistry: readonly SettingsSearchEntry[] = Object.freeze([
  ...AI_SETTINGS_DESCRIPTORS,
  ...AGENT_RUNTIME_DESCRIPTORS,
  ...PREFERENCES_DESCRIPTORS,
  ...REPOSITORIES_DESCRIPTORS,
  ...SYSTEM_DESCRIPTORS,
  ...TEMPLATES_DESCRIPTORS,
  ...WORKFLOW_DESCRIPTORS,
])
