import { describe, expect, it } from 'vitest'

import {
  TREATMENT_BY_FAMILY,
  familyFor,
  statusTreatment,
  type SemanticFamily,
  type StatusKind,
  type StatusTreatment,
} from './index'

const FAMILIES_ORDER: readonly SemanticFamily[] = ['success', 'warning', 'info', 'danger', 'muted']

describe('shared/status-presentation familyFor', () => {
  describe('issue-health kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['active', 'info'],
      ['paused', 'muted'],
      ['blocked', 'danger'],
      ['interrupted', 'warning'],
      ['cancelled', 'muted'],
      ['done', 'success'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('issue-health', state)).toBe(expected)
    })

    it('done maps to success (terminal completion reservation)', () => {
      expect(familyFor('issue-health', 'done')).toBe('success')
    })
  })

  describe('workflow-run kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['created', 'muted'],
      ['pending', 'muted'],
      ['ready', 'info'],
      ['running', 'info'],
      ['awaiting-approval', 'warning'],
      ['paused', 'muted'],
      ['stopped', 'muted'],
      ['completed', 'success'],
      ['failed', 'danger'],
      ['drift', 'warning'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('workflow-run', state)).toBe(expected)
    })

    it('completed maps to success', () => {
      expect(familyFor('workflow-run', 'completed')).toBe('success')
    })
  })

  describe('workflow-stage kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['pending', 'muted'],
      ['running', 'info'],
      ['awaiting-approval', 'warning'],
      ['passed', 'success'],
      ['failed', 'danger'],
      ['skipped', 'muted'],
      ['interrupted', 'warning'],
      ['not-started', 'muted'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('workflow-stage', state)).toBe(expected)
    })
  })

  describe('approval kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['pending', 'warning'],
      ['awaiting', 'warning'],
      ['approved', 'success'],
      ['rejected', 'danger'],
      ['error', 'danger'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('approval', state)).toBe(expected)
    })

    it('approved maps to success', () => {
      expect(familyFor('approval', 'approved')).toBe('success')
    })
  })

  describe('runner kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['idle', 'success'],
      ['busy', 'info'],
      ['stale', 'warning'],
      ['offline', 'muted'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('runner', state)).toBe(expected)
    })

    it('idle maps to success (healthy/available reservation)', () => {
      expect(familyFor('runner', 'idle')).toBe('success')
    })
  })

  describe('severity kind', () => {
    const cases: Array<[string, SemanticFamily]> = [
      ['ERROR', 'danger'],
      ['WARN', 'warning'],
      ['INFO', 'info'],
      ['DEBUG', 'muted'],
    ]
    it.each(cases)('%s -> %s', (state, expected) => {
      expect(familyFor('severity', state)).toBe(expected)
    })
  })

  describe('unknown / unmapped state fallback', () => {
    it('returns muted for a brand-new workflow status', () => {
      expect(familyFor('workflow-run', 'paused-and-throttled')).toBe('muted')
    })

    it('returns muted for null', () => {
      expect(familyFor('workflow-run', null)).toBe('muted')
    })

    it('returns muted for undefined', () => {
      expect(familyFor('workflow-run', undefined)).toBe('muted')
    })

    it('returns muted for empty string', () => {
      expect(familyFor('issue-health', '')).toBe('muted')
    })

    it('returns muted for every kind on an unmapped state', () => {
      const kinds: StatusKind[] = ['issue-health', 'workflow-run', 'workflow-stage', 'approval', 'runner', 'severity']
      for (const kind of kinds) {
        expect(familyFor(kind, '__totally_unknown_state__')).toBe('muted')
      }
    })
  })

  describe('reservation invariants', () => {
    it('success is the only family used for the completed/done meaning', () => {
      const successStates: Array<[StatusKind, string]> = [
        ['issue-health', 'done'],
        ['workflow-run', 'completed'],
        ['workflow-stage', 'passed'],
        ['approval', 'approved'],
        ['runner', 'idle'],
      ]
      for (const [kind, state] of successStates) {
        expect(familyFor(kind, state), `${kind}/${state} should resolve to success`).toBe('success')
      }
    })

    it('running shares the info family across workflow-run and workflow-stage', () => {
      expect(familyFor('workflow-run', 'running')).toBe('info')
      expect(familyFor('workflow-stage', 'running')).toBe('info')
    })

    it('awaiting-approval shares the warning family across workflow-run and workflow-stage', () => {
      expect(familyFor('workflow-run', 'awaiting-approval')).toBe('warning')
      expect(familyFor('workflow-stage', 'awaiting-approval')).toBe('warning')
    })

    it('failed shares the danger family across workflow-run and workflow-stage', () => {
      expect(familyFor('workflow-run', 'failed')).toBe('danger')
      expect(familyFor('workflow-stage', 'failed')).toBe('danger')
    })

    it('cancelled maps to muted for both issue-health and a workflow-run alias', () => {
      expect(familyFor('issue-health', 'cancelled')).toBe('muted')
      expect(familyFor('workflow-run', 'cancelled')).toBe('muted')
    })
  })
})

describe('shared/status-presentation TREATMENT_BY_FAMILY', () => {
  it('defines a record for every semantic family', () => {
    for (const family of FAMILIES_ORDER) {
      expect(TREATMENT_BY_FAMILY[family], `${family} should exist`).toBeDefined()
    }
  })

  it('every family\'s container / text / border / dot references semantic-token utilities only', () => {
    for (const family of FAMILIES_ORDER) {
      const treatment = TREATMENT_BY_FAMILY[family]
      expect(treatment.container, `${family} container references tokens`).toMatch(
        new RegExp(`(\\bbg-${family}-subtle\\b|\\bbg-muted\\b)`),
      )
      expect(treatment.text, `${family} text references tokens`).toMatch(
        new RegExp(`(\\btext-${family}\\b|\\btext-muted-foreground\\b)`),
      )
      expect(treatment.border, `${family} border references tokens`).toMatch(
        new RegExp(`(\\bborder-${family}-border\\b|\\bborder-border\\b)`),
      )
      expect(treatment.dot, `${family} dot references tokens`).toMatch(
        new RegExp(`(\\bbg-${family}\\b|\\bbg-muted-foreground\\b)`),
      )
    }
  })

  it('the success family container / border use success tokens (not emerald)', () => {
    const treatment = TREATMENT_BY_FAMILY.success
    expect(treatment.container).toContain('bg-success-subtle')
    expect(treatment.container).toContain('text-success')
    expect(treatment.container).toContain('border-success-border')
    expect(treatment.border).toContain('border-success-border')
    expect(treatment.container).not.toContain('emerald')
    expect(treatment.border).not.toContain('emerald')
    expect(treatment.dot).not.toContain('emerald')
  })

  it('records are frozen so call sites cannot mutate the family table', () => {
    for (const family of FAMILIES_ORDER) {
      expect(Object.isFrozen(TREATMENT_BY_FAMILY[family]), `${family} record frozen`).toBe(true)
    }
    expect(Object.isFrozen(TREATMENT_BY_FAMILY)).toBe(true)
  })
})

describe('shared/status-presentation statusTreatment', () => {
  it('returns a frozen record with container / text / border / dot / family', () => {
    const treatment = statusTreatment('issue-health', 'blocked')
    expect(treatment).toMatchObject({
      family: 'danger',
    })
    expect(Object.isFrozen(treatment)).toBe(true)
    expect(typeof treatment.container).toBe('string')
    expect(typeof treatment.text).toBe('string')
    expect(typeof treatment.border).toBe('string')
    expect(typeof treatment.dot).toBe('string')
  })

  it('derives the dot class from the same family as the text/background', () => {
    const cases: Array<[StatusKind, string, SemanticFamily]> = [
      ['issue-health', 'done', 'success'],
      ['issue-health', 'blocked', 'danger'],
      ['issue-health', 'interrupted', 'warning'],
      ['issue-health', 'active', 'info'],
      ['workflow-run', 'running', 'info'],
      ['workflow-run', 'completed', 'success'],
      ['runner', 'idle', 'success'],
      ['runner', 'busy', 'info'],
      ['runner', 'stale', 'warning'],
      ['runner', 'offline', 'muted'],
      ['severity', 'ERROR', 'danger'],
      ['severity', 'DEBUG', 'muted'],
    ]
    for (const [kind, state, expectedFamily] of cases) {
      const treatment = statusTreatment(kind, state)
      expect(treatment.family).toBe(expectedFamily)
      expect(
        treatment.dot.includes(`bg-${expectedFamily}`) ||
          (expectedFamily === 'muted' && treatment.dot.includes('bg-muted-foreground')),
        `${kind}/${state} dot derives from family`,
      ).toBe(true)
      expect(
        treatment.text.includes(`text-${expectedFamily}`) ||
          (expectedFamily === 'muted' && treatment.text.includes('text-muted-foreground')),
        `${kind}/${state} text derives from family`,
      ).toBe(true)
    }
  })

  it('container and border derive from the same family as the dot', () => {
    const treatment = statusTreatment('workflow-run', 'completed')
    expect(treatment.container).toContain('border-success-border')
    expect(treatment.border).toBe('border-success-border')
    expect(treatment.dot).toContain('bg-success')
  })

  it('container, text, border, and dot never reference raw Tailwind palette classes', () => {
    const kinds: StatusKind[] = ['issue-health', 'workflow-run', 'workflow-stage', 'approval', 'runner', 'severity']
    const states = [
      'active', 'paused', 'blocked', 'interrupted', 'cancelled', 'done',
      'created', 'pending', 'ready', 'running', 'awaiting-approval', 'stopped', 'completed', 'failed', 'drift',
      'not-started', 'passed', 'skipped',
      'pending', 'awaiting', 'approved', 'rejected', 'error',
      'idle', 'busy', 'stale', 'offline',
      'ERROR', 'WARN', 'INFO', 'DEBUG',
    ]
    for (const kind of kinds) {
      for (const state of states) {
        const treatment = statusTreatment(kind, state)
        for (const slot of ['container', 'text', 'border', 'dot'] as const) {
          const value = treatment[slot]
          for (const palette of ['emerald', 'green', 'blue', 'amber', 'red', 'slate', 'gray', 'orange', 'violet', 'cyan', 'yellow']) {
            expect(value.includes(`-${palette}-`), `${kind}/${state} ${slot} has no raw palette class`).toBe(false)
          }
        }
      }
    }
  })

  it('falls back to the muted/unknown treatment for unmapped states (no throw)', () => {
    let treatment: StatusTreatment | undefined
    expect(() => {
      treatment = statusTreatment('workflow-run', '__brand_new_state__')
    }).not.toThrow()
    expect(treatment).toBeDefined()
    expect(treatment!.family).toBe('muted')
    expect(treatment!.container).toContain('bg-muted')
    expect(treatment!.text).toContain('text-muted-foreground')
    expect(treatment!.border).toContain('border-border')
  })

  it('falls back to muted for null and undefined state (no throw)', () => {
    expect(() => statusTreatment('issue-health', null)).not.toThrow()
    expect(() => statusTreatment('issue-health', undefined)).not.toThrow()
    expect(statusTreatment('issue-health', null).family).toBe('muted')
    expect(statusTreatment('issue-health', undefined).family).toBe('muted')
  })
})