import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi, type Mock } from 'vitest'
import { useEventsConnection } from '../src/shared/api/events-hub'
import { EVENT_TYPES, REVERSE_DNS_EVENT_TYPES, TRANSCRIPT_EVENT_TYPES } from '../src/shared/lib/canonical-event-types'

type OnArgs = [string, (...payload: unknown[]) => void]
type InvokeArgs = [string, ...unknown[]]
type ReconnectedArgs = [() => void]

const signalr = vi.hoisted(() => {
  const on = vi.fn() as unknown as Mock<(...args: OnArgs) => void>
  const onreconnected = vi.fn() as unknown as Mock<(...args: ReconnectedArgs) => void>
  const invoke = vi.fn(
    () => new Promise<unknown>((resolve) => resolve(undefined)),
  ) as unknown as Mock<(...args: InvokeArgs) => Promise<unknown>>
  const start = vi.fn(
    () => new Promise<unknown>((resolve) => resolve(undefined)),
  ) as unknown as Mock<(...args: unknown[]) => Promise<unknown>>
  const stop = vi.fn(
    () => new Promise<unknown>((resolve) => resolve(undefined)),
  ) as unknown as Mock<(...args: unknown[]) => Promise<unknown>>

  const connection = {
    on,
    onreconnected,
    onreconnecting: vi.fn(),
    onclose: vi.fn(),
    invoke,
    start,
    stop,
  }

  const builder = {
    withUrl: vi.fn(() => builder),
    withAutomaticReconnect: vi.fn(() => builder),
    configureLogging: vi.fn(() => builder),
    build: vi.fn(() => connection),
  }

  return {
    builder,
    connection,
    on,
    onreconnected,
    invoke,
    start,
    stop,
    HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
      return builder
    }),
    HubConnectionState: { Connected: 'Connected', Reconnecting: 'Reconnecting', Disconnected: 'Disconnected', Connecting: 'Connecting', Disconnecting: 'Disconnecting' },
    LogLevel: { Warning: 'Warning' },
    HubConnectionState: {
      Connected: 'Connected',
      Reconnecting: 'Reconnecting',
      Connecting: 'Connecting',
      Disconnected: 'Disconnected',
      Disconnecting: 'Disconnecting',
    },
  }
})

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: signalr.HubConnectionBuilder,
  HubConnectionState: signalr.HubConnectionState,
  LogLevel: signalr.LogLevel,
HubConnectionState: signalr.HubConnectionState,
}))

afterEach(() => {
  vi.clearAllMocks()
})

function getOnReconnectedCallback(): () => void {
  const calls = signalr.onreconnected.mock.calls as ReconnectedArgs[]
  expect(calls.length).toBeGreaterThan(0)
  const cb = calls[calls.length - 1]?.[0]
  expect(typeof cb).toBe('function')
  return cb
}

function getTranscriptHandler(): (envelope: unknown) => void {
  const calls = signalr.on.mock.calls as OnArgs[]
  const entry = calls.find(([eventName]) => eventName === 'OnTranscriptEvent')
  expect(entry).toBeDefined()
  const handler = entry![1]
  return handler as (envelope: unknown) => void
}

function getSubscribeCalls(): Array<{ method: string; args: unknown[] }> {
  return (signalr.invoke.mock.calls as InvokeArgs[]).map(([method, ...args]) => ({
    method,
    args,
  }))
}

describe('useEventsConnection subscription behavior', () => {
  it('invokes SetSubscriptionsAsync with the canonical EVENT_TYPES list after start resolves', async () => {
    renderHook(() => useEventsConnection('project-1', vi.fn()))

    await waitFor(() => {
      expect(signalr.invoke).toHaveBeenCalledWith(
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
      expect(signalr.invoke).toHaveBeenCalledWith(
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

    let invokeCount = 0
    signalr.invoke.mockImplementation(() => {
      invokeCount += 1
      if (invokeCount === 1) {
        return Promise.reject(new Error('transient hub error'))
      }
      return Promise.resolve(undefined)
    })

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

    expect(signalr.start).not.toHaveBeenCalled()
    expect(signalr.invoke).not.toHaveBeenCalled()
  })
})
