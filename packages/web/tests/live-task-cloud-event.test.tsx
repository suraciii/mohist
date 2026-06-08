import { describe, it, expect, vi, beforeEach } from 'vitest'
import { __testing__ } from '../src/app/providers/LiveTaskProvider'
import { dispatchRebaseEvent, onRebaseEvent } from '../src/entities/issue/model/rebase-events'

const { unwrapEnvelope } = __testing__

describe('unwrapEnvelope', () => {
  it('returns the data when given a CloudEvents 1.0.2 envelope', () => {
    const data = { issueId: '42', projectId: 'mohist' }
    const envelope = {
      type: 'stage_changed',
      data,
      id: 'evt-1',
      source: '/mohist/test',
      specVersion: '1.0',
    }
    expect(unwrapEnvelope(envelope)).toBe(data)
  })

  it('returns the raw object when given a back-compat raw payload', () => {
    const raw = { issueId: '42', projectId: 'mohist' }
    expect(unwrapEnvelope(raw)).toBe(raw)
  })

  it('returns empty record for null or undefined data', () => {
    expect(unwrapEnvelope(null)).toEqual({})
    expect(unwrapEnvelope(undefined)).toEqual({})
  })

  it('returns empty record when envelope data is non-object', () => {
    expect(unwrapEnvelope({
      type: 'x', data: 'string', id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
    expect(unwrapEnvelope({
      type: 'x', data: 42, id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
    expect(unwrapEnvelope({
      type: 'x', data: null, id: 'a', source: 'b', specVersion: '1.0',
    })).toEqual({})
  })

  it('extracts the nested payload for legacy back-compat shape', () => {
    // The old code path: any object with a 'payload' field returned the
    // payload. We still support that for unmigrated producers. The
    // structural check above covers the new CloudEvents path; the
    // legacy path here is documented as a back-compat fallback.
    const legacy = { type: 'tool_call', payload: { foo: 'bar' }, issueId: '42' }
    const result = unwrapEnvelope(legacy)
    expect(result).toEqual({ foo: 'bar' })
  })

  it('returns the envelope as-is when only the CloudEvents marker is partial', () => {
    // Malformed: missing 'source' — falls through to the legacy check
    // (which requires 'payload'), and since there's no payload, returns
    // the whole object. The point is: it does NOT silently treat the
    // partial envelope as a payload and drop fields.
    const partial = { type: 'x', id: 'a', data: { foo: 'bar' } }
    expect(unwrapEnvelope(partial)).toBe(partial)
  })

  it('returns the envelope as-is when missing type', () => {
    // Malformed: missing 'type' is the common bug class
    const noType = { id: 'a', source: 'b', specVersion: '1.0', data: { foo: 'bar' } }
    expect(unwrapEnvelope(noType)).toBe(noType)
  })

  it('returns the envelope as-is when missing required envelope fields', () => {
    // Malformed: missing 'source'
    const partial = { type: 'x', id: 'a', data: { foo: 'bar' } }
    expect(unwrapEnvelope(partial)).toBe(partial)
  })

  it('returns the envelope as-is when missing type', () => {
    // Malformed: missing 'type' is the common bug class
    const noType = { id: 'a', source: 'b', specVersion: '1.0', data: { foo: 'bar' } }
    expect(unwrapEnvelope(noType)).toBe(noType)
  })
})

describe('rebase events reach onRebaseEvent listeners', () => {
  beforeEach(() => {
    // The dispatch target is a module-level EventTarget. Listeners from
    // previous tests are not torn down here because dispatchRebaseEvent
    // is not in the test's import path; this is a focused test.
  })

  it('forwards rebase_started to a registered listener', () => {
    const seen: unknown[] = []
    const off = onRebaseEvent((e) => seen.push(e))
    // Drive the dispatch path the way LiveTaskProvider would
    const envelope = {
      type: 'rebase_started',
      data: { issueId: 'i1', projectId: 'p1', issueNumber: 42 },
      id: 'evt-rb-1',
      source: '/mohist/test',
      specVersion: '1.0',
    }
    const payload = unwrapEnvelope(envelope) as { issueNumber: number }
    dispatchRebaseEvent({ type: 'rebase_started', issueNumber: payload.issueNumber })
    off()
    expect(seen).toEqual([{ type: 'rebase_started', issueNumber: 42 }])
  })
})

// silence: vi not used in this minimal set; left for future expansion
void vi
