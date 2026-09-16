import { describe, expect, it } from 'vitest'
import { buildHaystack, groupEntriesByTab } from './SettingsSearch'
import { settingsSearchRegistry } from '../model/settings-search-registry'

describe('buildHaystack', () => {
  it('lowercases and concatenates label, description, and placeholder', () => {
    expect(
      buildHaystack({
        tab: 'scheduling',
        label: 'Session Timeout',
        description: 'Maximum total time an Agent session can run.',
        focusTargetId: 'agent-runtime-timeout',
      }),
    ).toBe('session timeout maximum total time an agent session can run.')
  })

  it('includes the placeholder when one is present', () => {
    expect(
      buildHaystack({
        tab: 'scheduling',
        label: 'Poll Interval',
        description: 'Shorter = more realtime but higher CPU/network.',
        placeholder: 'Seconds',
        focusTargetId: 'agent-runtime-pollInterval',
      }),
    ).toContain('seconds')
  })

  it('excludes live numeric values from every registered entry', () => {
    for (const entry of settingsSearchRegistry) expect(buildHaystack(entry)).not.toMatch(/\b30\b/)
  })
})

describe('groupEntriesByTab', () => {
  it('groups every settings tab with a descriptor', () => {
    const labels = groupEntriesByTab(settingsSearchRegistry).map((group) => group.label)
    for (const label of ['Scheduling', 'Preferences', 'Repositories', 'System', 'Templates']) {
      expect(labels).toContain(label)
    }
  })

  it('preserves the order of scheduling settings', () => {
    const scheduling = groupEntriesByTab(settingsSearchRegistry).find((group) => group.tab === 'scheduling')
    expect(scheduling?.entries.map((entry) => entry.focusTargetId)).toEqual([
      'agent-runtime-timeout',
      'agent-runtime-stageTimeout',
      'agent-runtime-taskTimeout',
      'agent-runtime-maxConcurrent',
      'agent-runtime-pollInterval',
      'agent-runtime-maxGracePeriods',
    ])
  })
})
