/**
 * Single source of truth for keyboard shortcuts that the Settings surface
 * (Preferences tab reference, settings search palette, etc.) advertises.
 *
 * The array is the declarative side: every entry describes one shortcut the
 * application actually has (or is about to have in the same change). It is
 * the *only* place the Preferences "Keyboard shortcuts" reference reads from
 * so the reference cannot drift toward "fake" shortcuts.
 *
 * The actual keystroke bindings are registered by their owning modules —
 * `shared/ui/components/sidebar.tsx` registers `sidebar-toggle`, and the
 * settings search dialog (T-004) will register `settings-search` — by
 * calling `registerShortcutHandler(id, fn)`. The accompanying unit test
 * enforces that every id in `SHORTCUTS` resolves through
 * `getShortcutHandler` after a handler has been registered for it, so a
 * missing or stale entry is caught at test time rather than shipping a
 * reference that points to nothing.
 *
 * The registry is process-local and intentionally tiny: there is no global
 * shortcut bus, no event dispatch, no escape-hatch. Each owner of a shortcut
 * owns its keystroke listener (the sidebar already does; the settings
 * search dialog will). This module only provides the declarative catalogue
 * and the lookup helper the reference uses to verify an id is "real".
 */

export type ShortcutId = 'sidebar-toggle' | 'settings-search'

export interface ShortcutDefinition {
  id: ShortcutId
  label: string
  keys: string
  description: string
}

export const SHORTCUTS: readonly ShortcutDefinition[] = Object.freeze([
  {
    id: 'sidebar-toggle',
    label: 'Toggle sidebar',
    keys: '⌘B / Ctrl+B',
    description: 'Show or hide the navigation sidebar.',
  },
  {
    id: 'settings-search',
    label: 'Settings search',
    keys: '⌘K / Ctrl+K',
    description: 'Open the settings search palette.',
  },
])

const handlers = new Map<ShortcutId, () => void>()

/**
 * Register the runtime handler for a shortcut id. Returns an unregister
 * function that callers should invoke on teardown so the registry does not
 * leak handlers across mounts. Re-registering the same id overwrites the
 * previous handler, which lets T-004 (search) claim `settings-search`
 * without the Preferences reference's stub lingering.
 */
export function registerShortcutHandler(id: ShortcutId, handler: () => void): () => void {
  handlers.set(id, handler)
  return () => {
    if (handlers.get(id) === handler) {
      handlers.delete(id)
    }
  }
}

/**
 * Look up the currently registered handler for a shortcut id, or `undefined`
 * if no module has registered one. The Preferences reference uses this in
 * its unit test to assert that every advertised id is backed by a real
 * handler, preventing drift toward "fake" shortcuts.
 */
export function getShortcutHandler(id: ShortcutId): (() => void) | undefined {
  return handlers.get(id)
}

/**
 * Test-only: clear the handler registry. Production code never needs to
 * unregister everything at once, so this is intentionally not part of the
 * public API and is only re-exported under the `__testing__` namespace.
 */
export function __resetShortcutHandlersForTesting(): void {
  handlers.clear()
}
