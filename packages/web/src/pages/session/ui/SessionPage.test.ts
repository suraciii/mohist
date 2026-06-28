import { describe, expect, it } from 'vitest'
import { isCurrentSiblingSession } from './SessionPage'

describe('isCurrentSiblingSession', () => {
  it('matches the current session by workflow session name', () => {
    expect(isCurrentSiblingSession({ id: 'session-id-1', sessionName: 'check' }, 'check')).toBe(true)
  })

  it('matches the current session by legacy session id route key', () => {
    expect(isCurrentSiblingSession({ id: 'session-id-1', sessionName: 'check' }, 'session-id-1')).toBe(true)
  })

  it('does not match unrelated keys', () => {
    expect(isCurrentSiblingSession({ id: 'session-id-1', sessionName: 'check' }, 'plan')).toBe(false)
    expect(isCurrentSiblingSession({ id: 'session-id-1', sessionName: 'check' }, null)).toBe(false)
  })
})
