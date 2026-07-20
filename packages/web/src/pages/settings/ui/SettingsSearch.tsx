/**
 * Settings-page-scoped search dialog.
 *
 * Renders the existing cmdk primitives (`CommandDialog` / `CommandInput` /
 * `CommandList` / `CommandEmpty` / `CommandGroup` / `CommandItem`) fed by the
 * central `settingsSearchRegistry`. The shortcut that opens the dialog is
 * bound locally by this component — never at the application root — so the
 * global ⌘K / Ctrl+K slot stays free for a future global command palette.
 *
 * Design decisions implemented here:
 *
 * - **D1 — ⌘K is local.** A `keydown` listener is registered in a
 *   `useEffect` while the component is mounted (i.e. while the Settings page
 *   is active). The listener is removed on unmount, so navigating away
 *   automatically releases the slot. No global handler is added.
 *
 * - **D3 — values are excluded from matching.** Each `CommandItem` sets an
 *   explicit `value` composed of the descriptor's `label`, `description`, and
 *   `placeholder`. cmdk filters on the `value` prop, so a search like "30"
 *   never matches a timeout field whose current value happens to be 30.
 *
 * - **D4 — Enter navigates and focuses after the target mounts.** The focus
 *   intent travels in React Router navigation state so it survives switches
 *   between application and project settings routes. The destination consumes
 *   it once, tries reveal + focus immediately, then retries on animation frames
 *   for up to ~500ms.
 *   If the target never mounts the navigation still happens, but we surface
 *   a warning instead of silently losing the focus step.
 */
import { useCallback, useEffect, useId, useMemo, useRef, useState } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/shared/ui/components/command'
import {
  useSettingsSectionPath,
  type SettingsSectionKey,
} from '../lib'
import { isProjectSection } from '../lib/sections'
import { registerShortcutHandler } from '@/shared/lib/keyboard-shortcuts'
import { settingsSearchRegistry } from '../model/settings-search-registry'
import type { SettingsSearchEntry, SettingsTab } from '../model/settings-search'

const SECTION_LABEL: Record<SettingsTab, string> = {
  ai: 'Coder Agent',
  agent: 'Runtime',
  repositories: 'Repositories',
  workflows: 'Workflows',
  templates: 'Templates',
  system: 'System',
  preferences: 'Preferences',
}

/**
 * FOCUS_POLL_TIMEOUT_MS bounds target discovery after an Enter
 * activation. Settings section content can mount after the route change; ~500ms
 * is plenty in practice but acts as a hard ceiling.
 */
const FOCUS_POLL_TIMEOUT_MS = 500

const NO_MATCHES_COPY = 'No matching settings'

interface GroupedEntries {
  tab: SettingsTab
  label: string
  entries: SettingsSearchEntry[]
}

interface PendingFocus {
  targetId: string
  revealEvent?: string
}

const PENDING_FOCUS_STATE_KEY = 'settingsSearchFocus'

function recordState(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? value as Record<string, unknown>
    : {}
}

function pendingFocusFromState(value: unknown): PendingFocus | null {
  const pending = recordState(value)[PENDING_FOCUS_STATE_KEY]
  if (pending === null || typeof pending !== 'object' || Array.isArray(pending)) return null
  const targetId = (pending as Record<string, unknown>).targetId
  if (typeof targetId !== 'string' || targetId.length === 0) return null
  const revealEvent = (pending as Record<string, unknown>).revealEvent
  return {
    targetId,
    revealEvent: typeof revealEvent === 'string' ? revealEvent : undefined,
  }
}

function groupEntriesByTab(entries: readonly SettingsSearchEntry[]): GroupedEntries[] {
  const groups = new Map<SettingsTab, SettingsSearchEntry[]>()
  for (const entry of entries) {
    const bucket = groups.get(entry.tab)
    if (bucket) bucket.push(entry)
    else groups.set(entry.tab, [entry])
  }
  const ordered: GroupedEntries[] = []
  for (const tab of Object.keys(SECTION_LABEL) as SettingsTab[]) {
    const entries = groups.get(tab)
    if (entries && entries.length > 0) {
      ordered.push({ tab, label: SECTION_LABEL[tab], entries })
    }
  }
  return ordered
}

/**
 * Build the cmdk haystack for a descriptor. Lower-cased so cmdk's
 * case-insensitive matcher does not need to fight input casing. Live values
 * are deliberately not included; a search of "30" must not match a timeout
 * field whose current value happens to be 30.
 */
function buildHaystack(entry: SettingsSearchEntry): string {
  return `${entry.label} ${entry.description} ${entry.placeholder ?? ''}`
    .trim()
    .toLowerCase()
}

function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false
  if (target.isContentEditable) return true
  const tag = target.tagName
  if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true
  if (target.getAttribute('role') === 'textbox') return true
  return false
}

function SettingsSearch() {
  const [open, setOpen] = useState(false)
  const navigate = useNavigate()
  const location = useLocation()
  const sectionPath = useSettingsSectionPath()
  const titleId = useId()

  const openDialog = useCallback(() => setOpen(true), [])

  useEffect(() => {
    return registerShortcutHandler('settings-search', openDialog)
  }, [openDialog])

  useEffect(() => {
    function handleKeyDown(event: KeyboardEvent) {
      if (event.key.toLowerCase() !== 'k') return
      if (!(event.metaKey || event.ctrlKey)) return
      if (event.altKey || event.shiftKey) return
      if (open) return
      if (isEditableTarget(event.target)) return
      event.preventDefault()
      openDialog()
    }

    window.addEventListener('keydown', handleKeyDown)
    return () => window.removeEventListener('keydown', handleKeyDown)
  }, [open, openDialog])

  const grouped = useMemo(() => groupEntriesByTab(settingsSearchRegistry), [])

  const cancelPendingFocus = useRef<(() => void) | null>(null)

  useEffect(() => () => cancelPendingFocus.current?.(), [])

  function focusTargetElement(targetId: string, revealEvent?: string) {
    cancelPendingFocus.current?.()
    let frameId: number | undefined
    let timeoutId: number | undefined
    const cleanup = () => {
      if (frameId !== undefined) window.cancelAnimationFrame(frameId)
      if (timeoutId !== undefined) window.clearTimeout(timeoutId)
      if (cancelPendingFocus.current === cleanup) cancelPendingFocus.current = null
    }
    const tryRevealAndFocus = () => {
      if (revealEvent) window.dispatchEvent(new CustomEvent(revealEvent))
      const element = document.getElementById(targetId)
      if (!element) {
        frameId = window.requestAnimationFrame(tryRevealAndFocus)
        return
      }
      element.scrollIntoView({ block: 'center', inline: 'nearest' })
      element.focus({ preventScroll: true })
      cleanup()
    }

    cancelPendingFocus.current = cleanup
    timeoutId = window.setTimeout(() => {
      cleanup()
      // eslint-disable-next-line no-console
      console.warn(`[SettingsSearch] focus target #${targetId} did not mount within ${FOCUS_POLL_TIMEOUT_MS}ms after navigation`)
    }, FOCUS_POLL_TIMEOUT_MS)
    tryRevealAndFocus()
  }

  useEffect(() => {
    const pendingFocus = pendingFocusFromState(location.state)
    if (!pendingFocus) return
    focusTargetElement(pendingFocus.targetId, pendingFocus.revealEvent)
    const nextState = { ...recordState(location.state) }
    delete nextState[PENDING_FOCUS_STATE_KEY]
    navigate(`${location.pathname}${location.search}${location.hash}`, {
      replace: true,
      state: Object.keys(nextState).length > 0 ? nextState : null,
    })
  }, [location.hash, location.pathname, location.search, location.state, navigate])

  const handleSelect = useCallback(
    (entry: SettingsSearchEntry) => {
      const targetPath = sectionPath(entry.tab as SettingsSectionKey)
      if (!targetPath) {
        return
      }
      setOpen(false)
      navigate(targetPath, {
        state: {
          ...recordState(location.state),
          [PENDING_FOCUS_STATE_KEY]: {
            targetId: entry.focusTargetId,
            revealEvent: entry.revealEvent,
          },
        },
      })
    },
    [location.state, navigate, sectionPath],
  )

  return (
    <CommandDialog
      open={open}
      onOpenChange={setOpen}
      title="Settings search"
      description="Search for a setting by label, description, or placeholder."
      className="sm:max-w-lg"
      contentProps={{ finalFocus: false }}
    >
      <div className="sr-only" id={titleId}>
        Settings search
      </div>
      <CommandInput
        autoFocus
        placeholder="Search settings…"
        aria-labelledby={titleId}
        data-testid="settings-search-input"
      />
      <CommandList data-testid="settings-search-list">
        <CommandEmpty data-testid="settings-search-empty">{NO_MATCHES_COPY}</CommandEmpty>
        {grouped.map((group) => (
          <CommandGroup key={group.tab} heading={group.label} data-testid={`settings-search-group-${group.tab}`}>
            {group.entries.map((entry) => {
              const disabled =
                isProjectSection(entry.tab as SettingsSectionKey) &&
                sectionPath(entry.tab as SettingsSectionKey) === null
              return (
                <CommandItem
                  key={entry.focusTargetId}
                  value={buildHaystack(entry)}
                  disabled={disabled}
                  onSelect={() => handleSelect(entry)}
                  data-testid={`settings-search-result-${entry.focusTargetId}`}
                  className="min-h-[44px] py-2"
                >
                  <span className="flex min-w-0 flex-1 flex-col gap-0.5 leading-tight">
                    <span className="text-sm font-medium text-foreground">{entry.label}</span>
                    <span className="text-xs text-foreground/70">{entry.description}</span>
                  </span>
                  <span className="ml-2 text-xs text-foreground/70 shrink-0">
                    {SECTION_LABEL[entry.tab]}
                  </span>
                </CommandItem>
              )
            })}
          </CommandGroup>
        ))}
      </CommandList>
    </CommandDialog>
  )
}

export { SettingsSearch, NO_MATCHES_COPY, buildHaystack, groupEntriesByTab, FOCUS_POLL_TIMEOUT_MS }
