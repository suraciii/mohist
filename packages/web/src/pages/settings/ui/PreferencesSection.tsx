/**
 * 7th Settings tab: real user preferences and read-only reference info.
 *
 * The tab is intentionally narrow in scope. It holds *only* items that are
 * genuinely controllable from the web (the light/dark/system theme
 * selector, backed by the ThemeProvider from T-001) and a read-only
 * keyboard-shortcut reference sourced from
 * `features/settings-search/keyboard-shortcuts.ts`. System facts (timezone,
 * CLI executable path) belong on the System tab; notification preferences
 * are deferred until a notification subsystem exists. The non-goal guard
 * in `PreferencesSection.test.tsx` fails the build if either creeps back in.
 */
import { useId } from 'react'
import { CardSection } from '@/shared/ui/components/card-section'
import { RadioGroup } from '@base-ui/react/radio-group'
import { Radio as RadioPrimitive } from '@base-ui/react/radio'
import { cn } from '@/shared/lib/utils'
import { THEME_OPTIONS, type ThemeOption } from '@/shared/lib/theme/theme'
import { useTheme } from '@/app/providers/ThemeProvider'
import type { SettingsSearchEntry } from '@/features/settings-search'
import { SHORTCUTS } from '@/features/settings-search/keyboard-shortcuts'
import { getSectionMeta } from '../lib/sections'
import { SettingsSection } from './SettingsSection'

/**
 * Stable focus target used by the settings search palette (T-004) when
 * the user activates a result that lands on the theme selector. The
 * RadioGroup itself is what the search focuses, so the element bearing
 * this id is the group container.
 */
const THEME_FOCUS_TARGET_ID = 'preferences-theme'

/**
 * The Preferences tab contributes a single searchable entry: the theme
 * selector. The keyboard-shortcut reference is read-only and not
 * configurable from search (it would be misleading to "find" something the
 * user cannot edit), so it is intentionally not represented here. The
 * settings-search registry aggregates this array alongside the other
 * section descriptors.
 */
export const PREFERENCES_DESCRIPTORS: SettingsSearchEntry[] = [
  {
    tab: 'preferences',
    label: 'Theme',
    description: 'Choose the application color scheme — light, dark, or follow the operating system.',
    focusTargetId: THEME_FOCUS_TARGET_ID,
  },
]

const THEME_LABELS: Record<ThemeOption, string> = {
  light: 'Light',
  dark: 'Dark',
  system: 'System',
}

const THEME_DESCRIPTIONS: Record<ThemeOption, string> = {
  light: 'Always use the light theme.',
  dark: 'Always use the dark theme.',
  system: 'Match the operating system preference.',
}

function ThemeSelector({ id }: { id: string }) {
  const { theme, setTheme } = useTheme()
  const labelId = useId()
  const descriptionId = useId()
  const groupId = useId()

  return (
    <div className="space-y-2">
      <div
        id={labelId}
        className="text-xs font-medium text-muted-foreground"
      >
        Theme
      </div>
      <p
        id={descriptionId}
        className="text-xs text-muted-foreground"
      >
        Switch the application color scheme. Changes apply immediately and persist across sessions.
      </p>
      <div id={id} tabIndex={-1} className="rounded-md outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50">
        <RadioGroup
          value={theme}
          onValueChange={(value) => {
            if (value === 'light' || value === 'dark' || value === 'system') {
              setTheme(value)
            }
          }}
          aria-labelledby={labelId}
          aria-describedby={descriptionId}
          className="flex flex-wrap gap-2"
        >
          {THEME_OPTIONS.map((option) => {
            const optionLabel = THEME_LABELS[option]
            const optionDescription = THEME_DESCRIPTIONS[option]
            return (
              <label
                key={option}
                htmlFor={`${groupId}-${option}`}
                className="flex flex-1 min-w-[7rem] cursor-pointer items-start gap-2 rounded-md border bg-background p-3 min-h-[44px] transition-colors hover:bg-muted/40 has-[input:checked]:border-blue-500 has-[input:checked]:bg-blue-50 has-[input:checked]:text-blue-900 dark:has-[input:checked]:bg-blue-950/40 dark:has-[input:checked]:text-blue-100"
                data-testid={`preferences-theme-option-${option}`}
              >
                <RadioPrimitive.Root
                  value={option}
                  id={`${groupId}-${option}`}
                  aria-label={`${optionLabel} theme — ${optionDescription}`}
                  className="relative mt-0.5 flex size-4 shrink-0 items-center justify-center rounded-full border border-input bg-background outline-none transition-colors focus-visible:ring-[3px] focus-visible:ring-ring/50 data-[checked]:border-blue-600 data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50 dark:bg-input/30"
                >
                  <RadioPrimitive.Indicator className="flex size-2 items-center justify-center rounded-full bg-blue-600" />
                </RadioPrimitive.Root>
                <span className="flex min-w-0 flex-1 flex-col gap-0.5 leading-tight">
                  <span className="text-sm font-medium">{optionLabel}</span>
                  <span className="text-xs text-muted-foreground">{optionDescription}</span>
                </span>
              </label>
            )
          })}
        </RadioGroup>
      </div>
    </div>
  )
}

function KeyboardShortcutReference() {
  return (
    <ul
      className="divide-y divide-border"
      data-testid="preferences-keyboard-shortcuts"
      aria-label="Keyboard shortcuts"
    >
      {SHORTCUTS.map((shortcut) => (
        <li
          key={shortcut.id}
          className="flex items-center justify-between gap-3 py-2"
          data-testid={`preferences-shortcut-${shortcut.id}`}
        >
          <div className="flex min-w-0 flex-1 flex-col gap-0.5">
            <span className="text-sm font-medium text-foreground">{shortcut.label}</span>
            <span className="text-xs text-muted-foreground">{shortcut.description}</span>
          </div>
          <kbd
            aria-hidden="true"
            className={cn(
              'inline-flex shrink-0 items-center rounded-md border border-border bg-muted px-2 py-0.5',
              'font-mono text-xs text-foreground',
            )}
          >
            {shortcut.keys}
          </kbd>
        </li>
      ))}
    </ul>
  )
}

export function PreferencesSection() {
  const { theme } = useTheme()
  const { label: sectionLabel, description: sectionDescription } = getSectionMeta('preferences')

  return (
    <SettingsSection title={sectionLabel} description={sectionDescription}>
      <CardSection
        title="Theme"
        titleAs="h3"
        tone="default"
        data-testid="preferences-theme-card"
      >
        <ThemeSelector id={THEME_FOCUS_TARGET_ID} />
        <p
          aria-live="polite"
          className="text-xs text-muted-foreground"
          data-testid="preferences-theme-current"
        >
          Current: {THEME_LABELS[theme]}.{' '}
          {theme === 'system' ? 'Follows your operating system preference.' : 'Stored preference applied.'}
        </p>
      </CardSection>

      <CardSection
        title="Keyboard shortcuts"
        titleAs="h3"
        tone="default"
        data-testid="preferences-shortcuts-card"
      >
        <p className="mb-2 text-xs text-muted-foreground">
          The shortcuts available across the application. This list is read-only — bindings cannot be edited here.
        </p>
        <KeyboardShortcutReference />
      </CardSection>
    </SettingsSection>
  )
}
