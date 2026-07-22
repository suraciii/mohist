export type SettingsSectionKey =
  | 'ai'
  | 'agent'
  | 'repositories'
  | 'workflows'
  | 'templates'
  | 'label-catalog'
  | 'inbox'
  | 'system'
  | 'preferences'

export type SettingsSectionScope = 'application' | 'project'

export const SETTINGS_SECTION_KEYS: readonly SettingsSectionKey[] = [
  'ai',
  'agent',
  'repositories',
  'workflows',
  'templates',
  'label-catalog',
  'inbox',
  'system',
  'preferences',
]

const PROJECT_SECTION_KEYS: ReadonlySet<SettingsSectionKey> = new Set([
  'repositories',
  'workflows',
  'templates',
  'label-catalog',
  'inbox',
])

export function isSettingsSectionKey(value: string): value is SettingsSectionKey {
  return (SETTINGS_SECTION_KEYS as readonly string[]).includes(value)
}

export function sectionScope(key: SettingsSectionKey): SettingsSectionScope {
  return PROJECT_SECTION_KEYS.has(key) ? 'project' : 'application'
}

export function isApplicationSection(key: SettingsSectionKey): boolean {
  return sectionScope(key) === 'application'
}

export function isProjectSection(key: SettingsSectionKey): boolean {
  return sectionScope(key) === 'project'
}
