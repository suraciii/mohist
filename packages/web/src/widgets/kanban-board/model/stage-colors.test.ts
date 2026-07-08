import { describe, expect, it } from 'vitest'
import { IssueStatus } from '../../../entities/issue'
import { getStageColors, STAGE_COLORS, STAGE_FAMILY_RESERVATION } from './stage-colors'

const HEX_PATTERN = /#[0-9a-f]{3,8}\b/i

function allClassStrings(scheme: { accent: string; labelClass: string; activeBg: string; activeBorder: string; bottomBorder: string }): string[] {
  return [scheme.accent, scheme.labelClass, scheme.activeBg, scheme.activeBorder, scheme.bottomBorder]
}

describe('STAGE_COLORS - family reservation (per design D6)', () => {
  it('Backlog resolves to muted', () => {
    expect(STAGE_FAMILY_RESERVATION[IssueStatus.Backlog]).toBe('muted')
  })

  it('InProgress resolves to warning', () => {
    expect(STAGE_FAMILY_RESERVATION[IssueStatus.InProgress]).toBe('warning')
  })

  it('Done resolves to success', () => {
    expect(STAGE_FAMILY_RESERVATION[IssueStatus.Done]).toBe('success')
  })

  it('Cancelled resolves to danger', () => {
    expect(STAGE_FAMILY_RESERVATION[IssueStatus.Cancelled]).toBe('danger')
  })
})

describe('STAGE_COLORS - class strings, no inline hex', () => {
  it.each(Object.values(IssueStatus))('every field of stage %s is hex-free', (status) => {
    const scheme = STAGE_COLORS[status]
    for (const cls of allClassStrings(scheme)) {
      expect(cls).not.toMatch(HEX_PATTERN)
    }
  })

  it('accent is a token-backed `bg-<family>` class (not a raw palette like bg-green-500)', () => {
    expect(STAGE_COLORS[IssueStatus.Backlog].accent).toMatch(/^bg-(muted-foreground|muted)\b/)
    expect(STAGE_COLORS[IssueStatus.InProgress].accent).toMatch(/^bg-warning\b/)
    expect(STAGE_COLORS[IssueStatus.Done].accent).toMatch(/^bg-success\b/)
    expect(STAGE_COLORS[IssueStatus.Cancelled].accent).toMatch(/^bg-danger\b/)
  })

  it('labelClass uses the family text class (e.g. text-warning)', () => {
    expect(STAGE_COLORS[IssueStatus.InProgress].labelClass).toMatch(/^text-warning\b/)
    expect(STAGE_COLORS[IssueStatus.Done].labelClass).toMatch(/^text-success\b/)
    expect(STAGE_COLORS[IssueStatus.Cancelled].labelClass).toMatch(/^text-danger\b/)
  })

  it('activeBorder uses the family border class (e.g. border-warning-border)', () => {
    expect(STAGE_COLORS[IssueStatus.InProgress].activeBorder).toMatch(/^border-warning-border\b/)
    expect(STAGE_COLORS[IssueStatus.Done].activeBorder).toMatch(/^border-success-border\b/)
    expect(STAGE_COLORS[IssueStatus.Cancelled].activeBorder).toMatch(/^border-danger-border\b/)
  })

  it('bottomBorder uses direction-specific Tailwind border color utility', () => {
    expect(STAGE_COLORS[IssueStatus.InProgress].bottomBorder).toMatch(/^border-b-warning-border\b/)
    expect(STAGE_COLORS[IssueStatus.Done].bottomBorder).toMatch(/^border-b-success-border\b/)
    expect(STAGE_COLORS[IssueStatus.Cancelled].bottomBorder).toMatch(/^border-b-danger-border\b/)
  })

  it('bottomBorder is selected from explicit static classes so Tailwind can emit them', () => {
    expect(STAGE_COLORS[IssueStatus.Backlog].bottomBorder).toBe('border-b-border')
    expect(STAGE_COLORS[IssueStatus.InProgress].bottomBorder).toBe('border-b-warning-border')
    expect(STAGE_COLORS[IssueStatus.Done].bottomBorder).toBe('border-b-success-border')
    expect(STAGE_COLORS[IssueStatus.Cancelled].bottomBorder).toBe('border-b-danger-border')
  })

  it('does not use raw text-amber-700 / bg-amber-50/60 / text-green-700 palette classes', () => {
    const all = Object.values(IssueStatus).flatMap((s) => allClassStrings(STAGE_COLORS[s]))
    for (const cls of all) {
      expect(cls).not.toMatch(/text-amber-700/)
      expect(cls).not.toMatch(/bg-amber-50/)
      expect(cls).not.toMatch(/text-green-700/)
      expect(cls).not.toMatch(/bg-green-50/)
      expect(cls).not.toMatch(/text-red-700/)
      expect(cls).not.toMatch(/bg-red-50/)
    }
  })
})

describe('getStageColors', () => {
  it('returns the Backlog scheme for an unknown status fallback', () => {
    const fallback = STAGE_COLORS[IssueStatus.Backlog]
    expect(getStageColors('not-a-status' as unknown as IssueStatus)).toEqual(fallback)
  })

  it('returns the scheme for each defined IssueStatus', () => {
    for (const status of Object.values(IssueStatus)) {
      expect(getStageColors(status)).toEqual(STAGE_COLORS[status])
    }
  })

  it('returns class strings for every scheme (no inline hex anywhere)', () => {
    for (const status of Object.values(IssueStatus)) {
      const scheme = getStageColors(status)
      for (const cls of allClassStrings(scheme)) {
        expect(typeof cls).toBe('string')
        expect(cls.length).toBeGreaterThan(0)
        expect(cls).not.toMatch(HEX_PATTERN)
      }
    }
  })
})
