import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import {
  SETTINGS_SECTIONS,
  getSectionMeta,
  isApplicationSection,
  isProjectSection,
  isSettingsSectionKey,
  sectionScope,
} from './sections'

describe('settings section SOT', () => {
  it('classifies ai/agent/system/preferences as application scope', () => {
    expect(isApplicationSection('ai')).toBe(true)
    expect(isApplicationSection('agent')).toBe(true)
    expect(isApplicationSection('system')).toBe(true)
    expect(isApplicationSection('preferences')).toBe(true)
    expect(sectionScope('ai')).toBe('application')
    expect(sectionScope('agent')).toBe('application')
    expect(sectionScope('system')).toBe('application')
    expect(sectionScope('preferences')).toBe('application')
  })

  it('classifies repositories/templates/label-catalog/workflows/inbox as project scope', () => {
    expect(isProjectSection('repositories')).toBe(true)
    expect(isProjectSection('templates')).toBe(true)
    expect(isProjectSection('label-catalog')).toBe(true)
    expect(isProjectSection('workflows')).toBe(true)
    expect(isProjectSection('inbox')).toBe(true)
    expect(sectionScope('repositories')).toBe('project')
    expect(sectionScope('templates')).toBe('project')
    expect(sectionScope('label-catalog')).toBe('project')
    expect(sectionScope('workflows')).toBe('project')
    expect(sectionScope('inbox')).toBe('project')
  })

  it('mutually excludes application and project for every known section', () => {
    for (const meta of SETTINGS_SECTIONS) {
      expect(isApplicationSection(meta.key)).toBe(!isProjectSection(meta.key))
    }
  })

  it('provides a meta entry with key, label, and scope for every known section', () => {
    for (const meta of SETTINGS_SECTIONS) {
      const looked = getSectionMeta(meta.key)
      expect(looked.key).toBe(meta.key)
      expect(looked.label).toBeTruthy()
      expect(['application', 'project']).toContain(looked.scope)
    }
  })

  it('isSettingsSectionKey narrows string to SettingsSectionKey', () => {
    expect(isSettingsSectionKey('ai')).toBe(true)
    expect(isSettingsSectionKey('not-a-section')).toBe(false)
  })

  it('getSectionMeta throws on unknown section to surface routing mistakes', () => {
    expect(() => getSectionMeta('not-a-section' as never)).toThrow(/unknown settings section/i)
  })
})