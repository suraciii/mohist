import { act, renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { useEventsConnection } from '../src/shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES, TRANSCRIPT_EVENT_TYPES } from '../src/shared/lib/canonical-event-types'
import { deferNextFakeConnectionStart, fakeConnections, rejectNextInvoke, waitForFakeConnection, type FakeConnection } from '../tests/support/signalr-fake'


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

async function renderConnectedHook(
  onEvent = vi.fn(),
  onTranscriptEvent?: (envelope: unknown) => void,
) {
  deferNextFakeConnectionStart()
  const rendered = renderHook(() => useEventsConnection('project-1', onEvent, onTranscriptEvent))
  const connection = await waitForFakeConnection()
  await act(async () => {
    connection.completeStart()
    await connection.waitForStart()
    await connection.waitForInvoke('SetSubscriptionsAsync')
  })
  expect(rendered.result.current.status).toBe('connected')
  expect(rendered.result.current.connection).toBe(connection)
  return { ...rendered, connection }
}

describe('useEventsConnection subscription behavior', () => {
  it('invokes SetSubscriptionsAsync with the canonical EVENT_TYPES list after start resolves', async () => {
    const { connection } = await renderConnectedHook()

    expect(vi.mocked(connection.invoke)).toHaveBeenCalledWith(
      'SetSubscriptionsAsync',
      expect.arrayContaining([...EVENT_TYPES]),
    )

    const subscribeCall = getSubscribeCalls().find(
      ({ method }) => method === 'SetSubscriptionsAsync',
    )
    expect(subscribeCall).toBeDefined()
    const subscribed = subscribeCall!.args[0] as string[]
    expect(subscribed).toEqual([...EVENT_TYPES])
    expect(subscribed).toContain(TRANSCRIPT_EVENT_TYPES[0])
    expect(subscribed).toContain(REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound)
    expect(subscribed.length).toBe(EVENT_TYPES.length)
    expect(subscribed).not.toContain('session.closed')
    expect(subscribed).not.toContain('session.followup_completed')
    expect(subscribed).not.toContain('session.followup_failed')
  })

  it('re-invokes SetSubscriptionsAsync with the canonical list when onreconnected fires', async () => {
    const { result, connection } = await renderConnectedHook()

    const initialSubscribeCount = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    ).length
    const initialReconnectVersion = result.current.reconnectVersion

    const onReconnected = getOnReconnectedCallback()
    await act(async () => {
      onReconnected()
      await connection.waitForInvoke('SetSubscriptionsAsync', initialSubscribeCount + 1)
    })

    const totalSubscribeCalls = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    ).length
    expect(totalSubscribeCalls).toBe(initialSubscribeCount + 1)
    expect(result.current.reconnectVersion).toBe(initialReconnectVersion + 1)

    const allSubscribeCalls = getSubscribeCalls().filter(
      ({ method }) => method === 'SetSubscriptionsAsync',
    )
    for (const call of allSubscribeCalls) {
      expect(call.args[0]).toEqual([...EVENT_TYPES])
    }
  })

  it('registers an OnTranscriptEvent handler that forwards envelopes to the supplied callback', async () => {
    const onTranscriptEvent = vi.fn()
    await renderConnectedHook(vi.fn(), onTranscriptEvent)

    const handler = getTranscriptHandler()
    const envelope = { type: 'coder_text_chunk', session: 's1', chunk: 'hi' }
    handler(envelope)

    expect(onTranscriptEvent).toHaveBeenCalledWith(envelope)
  })

  it('registers an OnTranscriptEvent handler even when no transcript callback is supplied', async () => {
    await renderConnectedHook()

    const handler = getTranscriptHandler()
    expect(() => handler({ type: 'coder_text_chunk' })).not.toThrow()
  })

  it('catches invoke failures and does not tear down the connection', async () => {
    const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
    const failure = new Error('transient hub error')
    let unmount: (() => void) | null = null
    try {
      rejectNextInvoke(failure)
      const rendered = await renderConnectedHook()
      unmount = rendered.unmount

      expect(warnSpy).toHaveBeenCalledOnce()
      expect(warnSpy).toHaveBeenCalledWith(
        '[EventsHub] SetSubscriptionsAsync invoke failed (will not break connection):',
        failure,
      )

      const onReconnected = getOnReconnectedCallback()
      const initialReconnectVersion = rendered.result.current.reconnectVersion
      await act(async () => {
        onReconnected()
        await rendered.connection.waitForInvoke('SetSubscriptionsAsync', 2)
      })

      expect(getSubscribeCalls().filter(({ method }) => method === 'SetSubscriptionsAsync')).toHaveLength(2)
      expect(rendered.result.current.reconnectVersion).toBe(initialReconnectVersion + 1)
    } finally {
      unmount?.()
      warnSpy.mockRestore()
    }
  })

  it('does not invoke SetSubscriptionsAsync when projectId is null', async () => {
    const rendered = renderHook(() => useEventsConnection(null, vi.fn()))

    expect(fakeConnections.length).toBe(0)
    expect(rendered.result.current.status).toBe('disconnected')
  })
})
