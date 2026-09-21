import { describe, expect, it } from 'vitest'
import { setupProgressLabel } from './setup-progress'

describe('setupProgressLabel', () => {
  it('names the human step for every known setup phase', () => {
    expect(setupProgressLabel('create_app_credentials')).toBe('Approve install in Slack')
    expect(setupProgressLabel('waiting_for_slack_service')).toBe('Waiting for verification')
    expect(setupProgressLabel('fix_slack_setup')).toBe('Fix Slack setup')
    expect(setupProgressLabel('claim_owner')).toBe('Claim owner')
    expect(setupProgressLabel('complete')).toBe('Complete')
  })

  it('never asks the user to perform Server work', () => {
    for (const phase of Object.keys({ create_app_credentials: 0 })) {
      expect(setupProgressLabel(phase)).not.toMatch(/create|credential|manifest|socket hello/i)
    }
  })

  it('falls back to a readable form for an unknown phase', () => {
    expect(setupProgressLabel('some_new_phase')).toBe('some new phase')
    expect(setupProgressLabel(null)).toBe('Unknown')
  })
})
