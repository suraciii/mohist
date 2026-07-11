import { describe, it, expect } from 'vitest'
import { statusBadge, statusLabel } from './status-badge'
import { IssueHealth } from '../model/types'

describe('statusBadge', () => {
  it('returns green classes for Active', () => {
    expect(statusBadge(IssueHealth.Active)).toBe('text-green-700 bg-green-50')
  })

  it('returns amber classes for Paused', () => {
    expect(statusBadge(IssueHealth.Paused)).toBe('text-amber-700 bg-amber-50')
  })

  it('returns red classes for Blocked', () => {
    expect(statusBadge(IssueHealth.Blocked)).toBe('text-red-700 bg-red-50')
  })

  it('returns gray classes for unknown/default', () => {
    expect(statusBadge('unknown' as IssueHealth)).toBe('text-gray-700 bg-gray-50')
  })

  it('Active uses green-700 NOT green-600', () => {
    const result = statusBadge(IssueHealth.Active)
    expect(result).toContain('text-green-700')
    expect(result).toContain('bg-green-50')
    expect(result).not.toContain('green-600')
  })

  it('Blocked uses red-700 NOT red-600', () => {
    const result = statusBadge(IssueHealth.Blocked)
    expect(result).toContain('text-red-700')
    expect(result).not.toContain('red-600')
  })

  it('covers all IssueHealth enum values', () => {
    const allHealths: IssueHealth[] = [
      IssueHealth.Active,
      IssueHealth.Paused,
      IssueHealth.Blocked,
      IssueHealth.Cancelled,
      IssueHealth.Done,
    ]
    for (const s of allHealths) {
      const result = statusBadge(s)
      expect(result.length).toBeGreaterThan(0)
      expect(result).toMatch(/^text-\w+-\d+ bg-\w+-\d+$/)
    }
  })
})

describe('statusLabel', () => {
  it('uses issue lifecycle language for terminal issue healths', () => {
    expect(statusLabel(IssueHealth.Cancelled)).toBe('Cancelled')
    expect(statusLabel(IssueHealth.Done)).toBe('Done')
  })
})
