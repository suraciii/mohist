import { beforeEach, describe, expect, it } from 'vitest'
import { setScopedProperty } from '../../../../tests/support/scoped-property'
import { beginRecoveryRequest, completeRecoveryRequest } from './recoveryRequestIdentity'

const scope = { projectId: 'p1', sessionKey: 'issue-session:7:s1', operation: 'compact' } as const

describe('recovery request identity', () => {
  beforeEach(() => {
    sessionStorage.clear()
  })

  it('keeps one key per operation until the outcome is known', () => {
    const key = beginRecoveryRequest(scope)

    expect(key.length).toBeGreaterThan(0)
    expect(beginRecoveryRequest(scope)).toBe(key)
    expect(sessionStorage.length).toBe(1)

    completeRecoveryRequest(scope)

    expect(sessionStorage.length).toBe(0)
    expect(beginRecoveryRequest(scope)).not.toBe(key)
  })

  it('isolates identity by project, session, and operation', () => {
    const key = beginRecoveryRequest(scope)

    expect(beginRecoveryRequest({ ...scope, projectId: 'p2' })).not.toBe(key)
    expect(beginRecoveryRequest({ ...scope, sessionKey: 'issue-session:7:s2' })).not.toBe(key)
    expect(beginRecoveryRequest({ ...scope, operation: 'reset' })).not.toBe(key)
    expect(sessionStorage.length).toBe(4)
  })

  it('keeps one identity per page when sessionStorage is blocked', () => {
    setScopedProperty(window, 'sessionStorage', {
      configurable: true,
      get() {
        throw new Error('storage blocked')
      },
    })

    const key = beginRecoveryRequest({ ...scope, sessionKey: 'issue-session:7:blocked' })

    expect(key.length).toBeGreaterThan(0)
    expect(beginRecoveryRequest({ ...scope, sessionKey: 'issue-session:7:blocked' })).toBe(key)
  })
})
