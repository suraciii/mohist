import { describe, expect, it } from 'vitest'

import { contrastRatio, oklchToSrgb } from './color'
import { statusTreatment, type StatusKind } from './index'
import { FAMILIES, TOKENS, type Family, type Theme } from './tokens'

/**
 * The `muted` family does not appear in `TOKENS` (it is composed from the page
 * `muted` / `muted-foreground` tokens rather than the four semantic families).
 * These are the values `index.css` defines for `:root` (light) and `.dark`
 * (dark); the fixture guard test verifies them implicitly by guarding the
 * semantic-family tokens, but the muted pair is hard-coded here for clarity.
 */
const MUTED_BG = {
  light: { L: 0.97, C: 0, H: 0 },
  dark: { L: 0.269, C: 0, H: 0 },
} as const
const MUTED_FG = {
  light: { L: 0.52, C: 0, H: 0 },
  dark: { L: 0.708, C: 0, H: 0 },
} as const

interface Coverage {
  kind: StatusKind
  state: string
}

const COVERAGE: Coverage[] = [
  { kind: 'issue-health', state: 'blocked' },
  { kind: 'issue-health', state: 'cancelled' },
  { kind: 'issue-health', state: 'done' },
  { kind: 'issue-health', state: 'active' },
  { kind: 'issue-health', state: 'interrupted' },
  { kind: 'approval', state: 'awaiting' },
  { kind: 'approval', state: 'approved' },
  { kind: 'approval', state: 'rejected' },
  { kind: 'workflow-run', state: 'running' },
  { kind: 'workflow-run', state: 'completed' },
  { kind: 'workflow-run', state: 'failed' },
  { kind: 'workflow-run', state: 'awaiting-approval' },
  { kind: 'workflow-run', state: 'pending' },
  { kind: 'workflow-run', state: 'stopped' },
  { kind: 'workflow-stage', state: 'running' },
  { kind: 'workflow-stage', state: 'passed' },
  { kind: 'workflow-stage', state: 'awaiting-approval' },
  { kind: 'workflow-run', state: 'drift' },
  { kind: 'runner', state: 'idle' },
  { kind: 'runner', state: 'busy' },
  { kind: 'runner', state: 'stale' },
  { kind: 'runner', state: 'offline' },
  { kind: 'severity', state: 'ERROR' },
  { kind: 'severity', state: 'WARN' },
  { kind: 'severity', state: 'INFO' },
  { kind: 'severity', state: 'DEBUG' },
  { kind: 'context-health', state: 'green' },
  { kind: 'context-health', state: 'yellow' },
  { kind: 'context-health', state: 'red' },
]

const THEMES: Theme[] = ['light', 'dark']

const NORMAL_TEXT_AA = 4.5

function familyFromTreatment(kind: StatusKind, state: string) {
  return statusTreatment(kind, state).family
}

function familyTokens(family: Family | 'muted', theme: Theme) {
  if (family === 'muted') {
    return { bg: MUTED_BG[theme], fg: MUTED_FG[theme] }
  }
  return {
    bg: TOKENS[theme][family].subtle,
    fg: TOKENS[theme][family].base,
  }
}

// Reference the symbol so the import is not flagged as unused if a future
// refactor narrows the fixture usage.
void FAMILIES

describe('status-presentation contrast (WCAG AA)', () => {
  it.each(COVERAGE)(
    '$kind/$state background/foreground meets the documented WCAG AA threshold in both themes',
    ({ kind, state }) => {
      const family = familyFromTreatment(kind, state)
      for (const theme of THEMES) {
        const { bg, fg } = familyTokens(family, theme)
        const ratio = contrastRatio(oklchToSrgb(bg), oklchToSrgb(fg))
        expect(
          ratio,
          `${kind}/${state} ${theme} (family=${family}) contrast ${ratio.toFixed(3)} should be >= ${NORMAL_TEXT_AA}`,
        ).toBeGreaterThanOrEqual(NORMAL_TEXT_AA)
      }
    },
  )

  it('success / info / danger pairs clear strict 4.5:1 WCAG AA in both themes', () => {
    for (const theme of THEMES) {
      for (const family of ['success', 'info', 'danger'] as const) {
        const { bg, fg } = familyTokens(family, theme)
        const ratio = contrastRatio(oklchToSrgb(bg), oklchToSrgb(fg))
        expect(
          ratio,
          `${theme} ${family} pair contrast ${ratio.toFixed(3)} should be >= 4.5`,
        ).toBeGreaterThanOrEqual(NORMAL_TEXT_AA)
      }
    }
  })

  it('blocking and approval states meet 4.5:1 WCAG AA in both themes', () => {
    const blockingOrApproval: Coverage[] = [
      { kind: 'issue-health', state: 'blocked' },
      { kind: 'workflow-run', state: 'failed' },
      { kind: 'workflow-run', state: 'awaiting-approval' },
      { kind: 'approval', state: 'awaiting' },
      { kind: 'approval', state: 'rejected' },
    ]
    for (const { kind, state } of blockingOrApproval) {
      const family = familyFromTreatment(kind, state)
      expect(['danger', 'warning']).toContain(family)
      for (const theme of THEMES) {
        const { bg, fg } = familyTokens(family, theme)
        const ratio = contrastRatio(oklchToSrgb(bg), oklchToSrgb(fg))
        expect(
          ratio,
          `${kind}/${state} ${theme} (family=${family}) contrast ${ratio.toFixed(3)} should be >= ${NORMAL_TEXT_AA}`,
        ).toBeGreaterThanOrEqual(NORMAL_TEXT_AA)
      }
    }
  })

  it('warning and muted pairs also clear strict 4.5:1 WCAG AA in both themes', () => {
    for (const theme of THEMES) {
      for (const family of ['warning', 'muted'] as const) {
        const { bg, fg } = familyTokens(family, theme)
        const ratio = contrastRatio(oklchToSrgb(bg), oklchToSrgb(fg))
        expect(
          ratio,
          `${theme} ${family} pair contrast ${ratio.toFixed(3)} should be >= 4.5`,
        ).toBeGreaterThanOrEqual(NORMAL_TEXT_AA)
      }
    }
  })
})
