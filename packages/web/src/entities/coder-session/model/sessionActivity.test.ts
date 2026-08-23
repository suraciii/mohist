import { describe, expect, it } from 'vitest'
import { canFollowupSession, deriveSessionStatusKind } from './sessionActivity'

describe('session activity derivation', () => {
  it.each([
    ['idle', 'idle'],
    ['active', 'active'],
    ['unknown', 'unknown'],
    ['completed', 'unknown'],
    [undefined, 'unknown'],
  ])('maps %s to %s without terminal states', (input, expected) => {
    expect(deriveSessionStatusKind(input)).toBe(expected)
  })

  it('allows follow-up for idle and active activity', () => {
    expect(canFollowupSession('idle')).toBe(true)
    expect(canFollowupSession('active')).toBe(true)
    expect(canFollowupSession('unknown')).toBe(false)
  })
})
