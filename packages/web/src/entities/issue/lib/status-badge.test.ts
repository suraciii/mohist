import { describe, it, expect } from 'vitest'
import { statusBadge, statusLabel } from './status-badge'
import { IssueHealth } from '../model/types'
import { familyFor, statusTreatment } from '@/shared/status-presentation'

describe('statusBadge', () => {
  it('Active resolves through the shared layer to the info family', () => {
    const result = statusBadge(IssueHealth.Active)
    expect(familyFor('issue-health', IssueHealth.Active)).toBe('info')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Active).container)
  })

  it('Paused resolves through the shared layer to the muted family', () => {
    const result = statusBadge(IssueHealth.Paused)
    expect(familyFor('issue-health', IssueHealth.Paused)).toBe('muted')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Paused).container)
  })

  it('Blocked resolves through the shared layer to the danger family', () => {
    const result = statusBadge(IssueHealth.Blocked)
    expect(familyFor('issue-health', IssueHealth.Blocked)).toBe('danger')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Blocked).container)
  })

  it('Interrupted resolves through the shared layer to the warning family', () => {
    const result = statusBadge(IssueHealth.Interrupted)
    expect(familyFor('issue-health', IssueHealth.Interrupted)).toBe('warning')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Interrupted).container)
  })

  it('Done resolves through the shared layer to the success family', () => {
    const result = statusBadge(IssueHealth.Done)
    expect(familyFor('issue-health', IssueHealth.Done)).toBe('success')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Done).container)
  })

  it('Cancelled resolves through the shared layer to the muted family', () => {
    const result = statusBadge(IssueHealth.Cancelled)
    expect(familyFor('issue-health', IssueHealth.Cancelled)).toBe('muted')
    expect(result).toBe(statusTreatment('issue-health', IssueHealth.Cancelled).container)
  })

  it('unknown health resolves to the muted treatment (no throw)', () => {
    const result = statusBadge('unknown' as IssueHealth)
    expect(result).toBe(statusTreatment('issue-health', 'unknown').container)
    expect(result).toContain('bg-muted')
  })

  it('contains no raw Tailwind palette classes', () => {
    const palette = ['emerald', 'green-', 'amber-', 'red-', 'orange-', 'gray-']
    const allHealths: IssueHealth[] = [
      IssueHealth.Active,
      IssueHealth.Paused,
      IssueHealth.Blocked,
      IssueHealth.Interrupted,
      IssueHealth.Cancelled,
      IssueHealth.Done,
    ]
    for (const h of allHealths) {
      const result = statusBadge(h)
      for (const p of palette) {
        expect(result.includes(p), `palette ${p} should not appear for ${h}`).toBe(false)
      }
    }
  })

  it('covers all IssueHealth enum values without throwing', () => {
    const allHealths: IssueHealth[] = [
      IssueHealth.Active,
      IssueHealth.Paused,
      IssueHealth.Blocked,
      IssueHealth.Interrupted,
      IssueHealth.Cancelled,
      IssueHealth.Done,
    ]
    for (const s of allHealths) {
      const result = statusBadge(s)
      expect(result.length).toBeGreaterThan(0)
    }
  })
})

describe('statusLabel', () => {
  it('uses issue lifecycle language for terminal issue healths', () => {
    expect(statusLabel(IssueHealth.Cancelled)).toBe('Cancelled')
    expect(statusLabel(IssueHealth.Done)).toBe('Done')
  })
})