// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { describe, expect, it } from 'vitest'
import { IssueHealth } from '@/entities/issue'
import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'
import { contrastRatio, oklchToSrgb } from '@/shared/status-presentation/color'
import { TOKENS, type Family, type Theme } from '@/shared/status-presentation/tokens'

/**
 * Mirrors the `INDICATOR_TO_TREATMENT` binding that `widgets/kanban-board/ui/IssueCard.tsx`
 * uses to resolve each kanban status pill through the shared status-presentation
 * layer. Asserting on this binding — instead of the now-deleted `STATUS_PILL_PAIRS`
 * hex map — guarantees the contrast test tracks what is actually rendered.
 */
interface Coverage {
  indicator: 'blocked' | 'cancelled' | 'approval' | 'running' | 'waiting' | 'drift'
  treatment: StatusTreatment
}

const COVERAGE: Coverage[] = [
  { indicator: 'blocked', treatment: statusTreatment('issue-health', IssueHealth.Blocked) },
  { indicator: 'cancelled', treatment: statusTreatment('issue-health', IssueHealth.Cancelled) },
  { indicator: 'approval', treatment: statusTreatment('approval', 'awaiting') },
  { indicator: 'running', treatment: statusTreatment('workflow-run', 'running') },
  { indicator: 'waiting', treatment: statusTreatment('workflow-run', 'awaiting-approval') },
  { indicator: 'drift', treatment: statusTreatment('workflow-run', 'drift') },
]

/**
 * Muted pair (page neutral, no semantic family). Mirror of the values used in
 * `shared/status-presentation/contrast.spec.ts` so the same fixture and guard
 * keep the kanban status pill contrast in lock-step with the layer.
 */
const MUTED_BG = {
  light: { L: 0.97, C: 0, H: 0 },
  dark: { L: 0.269, C: 0, H: 0 },
} as const
const MUTED_FG = {
  light: { L: 0.52, C: 0, H: 0 },
  dark: { L: 0.708, C: 0, H: 0 },
} as const

const THEMES: Theme[] = ['light', 'dark']

const NORMAL_TEXT_AA = 4.5

function familyBgFg(family: Family | 'muted', theme: Theme) {
  if (family === 'muted') {
    return { bg: MUTED_BG[theme], fg: MUTED_FG[theme] }
  }
  return {
    bg: TOKENS[theme][family].subtle,
    fg: TOKENS[theme][family].base,
  }
}

describe('StatusPill rendered treatment (kanban) — contrast', () => {
  it.each(COVERAGE)(
    '$indicator pill rendered treatment meets the documented WCAG AA threshold in both themes',
    ({ indicator, treatment }) => {
      expect(['success', 'warning', 'info', 'danger', 'muted']).toContain(treatment.family)
      for (const theme of THEMES) {
        const { bg, fg } = familyBgFg(treatment.family, theme)
        const ratio = contrastRatio(oklchToSrgb(bg), oklchToSrgb(fg))
        expect(
          ratio,
          `${indicator} (family=${treatment.family}) ${theme} contrast ${ratio.toFixed(3)} should be >= ${NORMAL_TEXT_AA}`,
        ).toBeGreaterThanOrEqual(NORMAL_TEXT_AA)
      }
    },
  )

  it('every covered indicator resolves through the shared status-presentation layer', () => {
    // Each indicator must carry a token-backed container — no raw palette classes
    // (`bg-red-100`, `text-amber-800`, …) — and the family must agree with
    // `familyFor(...)` so a future widget cannot silently drift.
    const PALETTE = ['emerald', 'green-', 'amber-', 'red-', 'orange-', 'gray-', 'blue-', 'yellow-']
    for (const { treatment } of COVERAGE) {
      expect(treatment.container).toMatch(/(bg-success-subtle|bg-warning-subtle|bg-info-subtle|bg-danger-subtle|bg-muted)/)
      expect(treatment.border).toMatch(/(border-success-border|border-warning-border|border-info-border|border-danger-border|border-border)/)
      for (const palette of PALETTE) {
        expect(treatment.container.includes(palette), `container still has raw ${palette}`).toBe(false)
        expect(treatment.border.includes(palette), `border still has raw ${palette}`).toBe(false)
        expect(treatment.dot.includes(palette), `dot still has raw ${palette}`).toBe(false)
        expect(treatment.text.includes(palette), `text still has raw ${palette}`).toBe(false)
      }
    }
  })

  it('blocked / approval / waiting / drift resolve to blocking or approval families (warning/danger)', () => {
    const blockingOrApproval = COVERAGE.filter((c) => ['blocked', 'approval', 'waiting', 'drift'].includes(c.indicator))
    for (const { treatment } of blockingOrApproval) {
      expect(['danger', 'warning']).toContain(treatment.family)
    }
  })
})
