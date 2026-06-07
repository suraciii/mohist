import { describe, it, expect } from 'vitest'
import { __testing__ } from '../src/app/providers/LiveTaskProvider'

const { unwrapEnvelope } = __testing__

describe('unwrapEnvelope', () => {
  it('returns the payload when given a CloudEvent envelope', () => {
    const payload = { issueId: '42', projectId: 'mohist' }
    const envelope = {
      type: 'stage_changed',
      payload,
      id: 'evt-1',
      source: '/mohist/test',
      specVersion: '1.0',
    }
    expect(unwrapEnvelope(envelope)).toBe(payload)
  })

  it('returns the raw object when given a back-compat raw payload', () => {
    const raw = { issueId: '42', projectId: 'mohist' }
    expect(unwrapEnvelope(raw)).toBe(raw)
  })

  it('returns empty record for null or undefined data', () => {
    expect(unwrapEnvelope(null)).toEqual({})
    expect(unwrapEnvelope(undefined)).toEqual({})
  })

  it('returns empty record when envelope payload is non-object', () => {
    expect(unwrapEnvelope({ type: 'x', payload: 'string' })).toEqual({})
    expect(unwrapEnvelope({ type: 'x', payload: 42 })).toEqual({})
    expect(unwrapEnvelope({ type: 'x', payload: null })).toEqual({})
  })
})
