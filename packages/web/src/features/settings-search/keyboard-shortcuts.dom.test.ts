import { afterEach, describe, expect, it } from 'vitest'
import {
  SHORTCUTS,
  __resetShortcutHandlersForTesting,
  getShortcutHandler,
  registerShortcutHandler,
} from './keyboard-shortcuts'

afterEach(() => {
  __resetShortcutHandlersForTesting()
})

describe('SHORTCUTS', () => {
  it('exposes the two real shortcuts the application currently ships with', () => {
    expect(SHORTCUTS.map((s) => s.id)).toEqual(['sidebar-toggle', 'settings-search'])
  })

  it('freezes the array so callers cannot mutate the catalogue at runtime', () => {
    expect(Object.isFrozen(SHORTCUTS)).toBe(true)
  })

  it('requires every entry to have non-empty label, keys, and description', () => {
    for (const shortcut of SHORTCUTS) {
      expect(shortcut.id).toMatch(/^[a-z][a-z0-9-]*$/)
      expect(shortcut.label.length).toBeGreaterThan(0)
      expect(shortcut.keys.length).toBeGreaterThan(0)
      expect(shortcut.description.length).toBeGreaterThan(0)
    }
  })

  it('has a unique id per entry so the registry can never collide', () => {
    const ids = SHORTCUTS.map((s) => s.id)
    expect(new Set(ids).size).toBe(ids.length)
  })
})

describe('registerShortcutHandler', () => {
  it('returns a function that unregisters the handler when invoked', () => {
    const handler = () => {}
    const unregister = registerShortcutHandler('sidebar-toggle', handler)
    expect(getShortcutHandler('sidebar-toggle')).toBe(handler)

    unregister()
    expect(getShortcutHandler('sidebar-toggle')).toBeUndefined()
  })

  it('re-registering the same id overwrites the previous handler', () => {
    const first = () => {}
    const second = () => {}
    registerShortcutHandler('sidebar-toggle', first)
    registerShortcutHandler('sidebar-toggle', second)
    expect(getShortcutHandler('sidebar-toggle')).toBe(second)
  })

  it('only removes the matching handler on unregister, not a later registration', () => {
    const first = () => {}
    const second = () => {}
    const unregisterFirst = registerShortcutHandler('sidebar-toggle', first)
    registerShortcutHandler('sidebar-toggle', second)
    unregisterFirst()
    // The first handler's unregister function should NOT remove the second
    // handler that was registered afterwards.
    expect(getShortcutHandler('sidebar-toggle')).toBe(second)
  })
})
