import { describe, expect, it } from 'vitest'
import { __testing__ } from './LiveTaskProvider'

describe('LiveTaskProvider transcript routing', () => {
  it('routes persisted transcript segments through the same live detail events as chunks', () => {
    expect(__testing__.routeTranscriptEventName('agent_message')).toBe('coder_text_chunk')
    expect(__testing__.routeTranscriptEventName('agent_thought')).toBe('coder_thought_chunk')
    expect(__testing__.routeTranscriptEventName('agent_message_chunk')).toBe('coder_text_chunk')
    expect(__testing__.routeTranscriptEventName('agent_thought_chunk')).toBe('coder_thought_chunk')
  })

  it('unwraps transcript envelopes with runtime metadata and payload', () => {
    const envelope = {
      type: 'agent_message',
      sessionId: 'session-1',
      sequence: 12,
      createdAt: '2026-06-12T00:00:00.000Z',
      payload: { text: 'persisted segment' },
    }

    const unwrapped = __testing__.unwrapTranscriptEnvelope(envelope)

    expect(unwrapped?.eventName).toBe('agent_message')
    expect(unwrapped?.payload).toEqual({ text: 'persisted segment' })
    expect(unwrapped?.detail).toMatchObject({
      type: 'agent_message',
      text: 'persisted segment',
      payload: { text: 'persisted segment' },
      sequence: 12,
    })
  })

  it('normalizes server transcript metadata into live agent detail fields', () => {
    const unwrapped = __testing__.unwrapTranscriptEnvelope({
      type: 'agent_thought',
      sessionId: 'proj/wr/plan',
      issueNumber: 84,
      agentSessionId: 'acp-84',
      workId: 'T-008.3',
      payload: { text: 'thinking' },
    })

    expect(unwrapped?.detail).toMatchObject({
      issueId: '84',
      issueNumber: 84,
      acpSessionId: 'acp-84',
      coderSessionId: 'proj/wr/plan',
      executionId: 'T-008.3',
      text: 'thinking',
    })
  })
})
