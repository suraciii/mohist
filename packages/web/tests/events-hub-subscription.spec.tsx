import { renderHook, waitFor } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { useEventsConnection } from '../src/shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES, TRANSCRIPT_EVENT_TYPES } from '../src/shared/lib/canonical-event-types'
import { fakeConnections, rejectNextInvoke, type FakeConnection } from '../tests/support/signalr-fake'


function lastConnection(): FakeConnection {
  const conn = fakeConnections[fakeConnections.length - 1]
  if (!conn) throw new Error('no fake connection was built')
  return conn
}

function getOnReconnectedCallback(): () => void {
  const conn = lastConnection()
  const cb = conn.onreconnected() as (() => void) | null
  expect(typeof cb).toBe('function')
  return cb as () => void
}

function getTranscriptHandler(): (envelope: unknown) => void {
  const conn = lastConnection()
  const handler = conn.handlers.get('OnTranscriptEvent')
  expect(handler).toBeDefined()
  return handler as (envelope: unknown) => void
}

function getSubscribeCalls(): Array<{ method: string; args: unknown[] }> {
  return lastConnection().invokes
}

describe('useEventsConnection subscription behavior', () => {
  it('invokes SetSubscriptionsAsync with the canonical EVENT_TYPES list after start resolves', async () => {
    renderHook(() => useEventsConnection('project-1', vi.fn()))

    await waitFor(() => {
      expect(vi.mocked(lastConnection().invoke)).toHaveBeenCalledWith(
        'SetSubscriptionsAsync',
        expect.arrayContaining([...EVENT_TYPES]),
      )
    })

    const subscribeCall = getSubscribeCalls().find(
      ({ method }) => method === 'SetSubscriptionsAsync',
    )
    expect(subscribeCall).toBeDefined()
    const subscribed = subscribeCall!.args[0] as string[]
    expect(subscribed).toEqual([...EVENT_TYPES])
    expect(subscribed).toContain(TRANSCRIPT_EVENT_TYPES[0])
    expect(subscribed).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound)
    expect(subscribed.length).toBe(EVENT_TYPES.length)
  })

  it('re-invokes SetSubscriptionsAsync with the canonical list when onreconnected fires', async () => {
    renderHook(() => useEventsConnection('project-1', vi.fn()))

    await waitFor(() => {
      expect(vi.mocked(lastConnection().invoke)).toHaveBeenCalledWith(
        'SetSubscriptionsAsync',
        expect.any(Array),
      )
    })

    const initialSubscribeCount = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    ).length

    const onReconnected = getOnReconnectedCallback()
    onReconnected()

    await waitFor(() => {
      const totalSubscribeCalls = getSubscribeCalls().filter(
        ({ method }) => method === 'SetSubscriptionsAsync',
      ).length
      expect(totalSubscribeCalls).toBeGreaterThan(initialSubscribeCount)
    })

    const allSubscribeCalls = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    )
    for (const call of allSubscribeCalls) {
      expect(call.args[0]).toEqual([...EVENT_TYPES])
    }
  })

  it('registers an OnTranscriptEvent handler that forwards envelopes to the supplied callback', async () => {
    const onTranscriptEvent = vi.fn()
    renderHook(() => useEventsConnection('project-1', vi.fn(), onTranscriptEvent))

    const handler = getTranscriptHandler()
    const envelope = { type: 'coder_text_chunk', session: 's1', chunk: 'hi' }
    handler(envelope)

    expect(onTranscriptEvent).toHaveBeenCalledWith(envelope)
  })

  it('registers an OnTranscriptEvent handler even when no transcript callback is supplied', () => {
    renderHook(() => useEventsConnection('project-1', vi.fn()))

    const handler = getTranscriptHandler()
    expect(() => handler({ type: 'coder_text_chunk' })).not.toThrow()
  })

  it('catches invoke failures and does not tear down the connection', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const errorSpy = vi.spyOn(console, 'error').mockImplementation(() => {})

    rejectNextInvoke(new Error('transient hub error'))
    const { unmount } = renderHook(() => useEventsConnection('project-1', vi.fn()))

    await waitFor(() => {
      expect(warnSpy).toHaveBeenCalled()
    })

    const warnMessage = warnSpy.mock.calls.map((call) => String(call[0])).join('\n')
    expect(warnMessage).toContain('SetSubscriptionsAsync')

    const onReconnected = getOnReconnectedCallback()
    expect(() => onReconnected()).not.toThrow()

    const reconnectInvokeCalls = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    ).length
    expect(reconnectInvokeCalls).toBeGreaterThan(1)

    unmount()

    warnSpy.mockRestore()
    errorSpy.mockRestore()
  })

  it('does not invoke SetSubscriptionsAsync when projectId is null', async () => {
    renderHook(() => useEventsConnection(null, vi.fn()))

    await new Promise((resolve) => setTimeout(resolve, 0))

    expect(fakeConnections.length).toBe(0)
  })
})
