import { describe, it, expect } from 'vitest'
import { statusBadge, statusLabel } from './status-badge'
import { IssueStatus } from './types'

describe('statusBadge', () => {
  it('returns green classes for Active', () => {
    expect(statusBadge(IssueStatus.Active)).toBe('text-green-700 bg-green-50')
  })

  it('returns amber classes for Paused', () => {
    expect(statusBadge(IssueStatus.Paused)).toBe('text-amber-700 bg-amber-50')
  })

  it('returns red classes for Blocked', () => {
    expect(statusBadge(IssueStatus.Blocked)).toBe('text-red-700 bg-red-50')
  })

  it('returns orange classes for Interrupted', () => {
    expect(statusBadge(IssueStatus.Interrupted)).toBe('text-orange-700 bg-orange-50')
  })

  it('returns gray classes for unknown/default', () => {
    expect(statusBadge('unknown' as IssueStatus)).toBe('text-gray-700 bg-gray-50')
  })

  it('Active uses green-700 NOT green-600 (Issue #30 regression)', () => {
    const result = statusBadge(IssueStatus.Active)
    expect(result).toContain('text-green-700')
    expect(result).toContain('bg-green-50')
    expect(result).not.toContain('green-600')
  })

  it('Blocked uses red-700 NOT red-600 (Issue #30 regression)', () => {
    const result = statusBadge(IssueStatus.Blocked)
    expect(result).toContain('text-red-700')
    expect(result).not.toContain('red-600')
  })

  it('covers all IssueStatus enum values', () => {
    const allStatuses: IssueStatus[] = [
      IssueStatus.Active,
      IssueStatus.Paused,
      IssueStatus.Blocked,
      IssueStatus.Interrupted,
      IssueStatus.Cancelled,
      IssueStatus.Done,
    ]
    for (const s of allStatuses) {
      const result = statusBadge(s)
      expect(result.length).toBeGreaterThan(0)
      expect(result).toMatch(/^text-\w+-\d+ bg-\w+-\d+$/)
    }
  })
})

describe('statusLabel', () => {
  it('uses issue lifecycle language for terminal issue statuses', () => {
    expect(statusLabel(IssueStatus.Cancelled)).toBe('Cancelled')
    expect(statusLabel(IssueStatus.Done)).toBe('Done')
  })
})
