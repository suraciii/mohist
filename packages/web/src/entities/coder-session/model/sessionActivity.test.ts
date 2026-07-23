import { describe, expect, it } from 'vitest'
import { canFollowupSession, canRecoverSession, deriveSessionActivity, deriveSessionStatusKind } from './sessionActivity'

describe('session activity derivation', () => {
  it.each([
    ['idle', 'idle'],
    ['active', 'active'],
    ['unknown', 'unknown'],
    ['completed', 'unknown'],
    [undefined, 'unknown'],
  ])('maps %s to %s without terminal states', (input, expected) => {
    expect(deriveSessionActivity(input)).toBe(expected)
    expect(deriveSessionStatusKind(input)).toBe(expected)
  })

  it('allows follow-up for idle and active activity, but recovery actions only for idle', () => {
    expect(canFollowupSession('idle')).toBe(true)
    expect(canFollowupSession('active')).toBe(true)
    expect(canFollowupSession('unknown')).toBe(false)
    expect(canRecoverSession('idle')).toBe(true)
    expect(canRecoverSession('active')).toBe(false)
    expect(canRecoverSession('unknown')).toBe(false)
  })
})
