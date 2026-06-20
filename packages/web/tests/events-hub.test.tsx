import { renderHook } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { createEventsConnection, useEventsConnection } from '../src/shared/api/events-hub'

const signalr = vi.hoisted(() => {
  const connection = {
    on: vi.fn(),
    onreconnected: vi.fn(),
    onreconnecting: vi.fn(),
    onclose: vi.fn(),
    invoke: vi.fn(() => Promise.resolve()),
    start: vi.fn(() => Promise.resolve()),
    stop: vi.fn(() => Promise.resolve()),
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

describe('events hub SignalR client', () => {
  it('connects to the project-scoped SignalR hub', () => {
    createEventsConnection('proj/with space')

    expect(signalr.builder.withUrl).toHaveBeenCalledWith('/hubs/events?projectId=proj%2Fwith%20space')
    expect(signalr.builder.withAutomaticReconnect).toHaveBeenCalled()
    expect(signalr.builder.configureLogging).toHaveBeenCalledWith(signalr.LogLevel.Warning)
    expect(signalr.builder.build).toHaveBeenCalled()
  })

  it('routes OnEvent messages to the supplied callback and stops on cleanup', async () => {
    const onEvent = vi.fn()
    const { unmount } = renderHook(() => useEventsConnection('project-1', onEvent))

    expect(signalr.connection.on).toHaveBeenCalledWith('OnEvent', expect.any(Function))
    expect(signalr.connection.start).toHaveBeenCalled()

    const handler = signalr.connection.on.mock.calls[0][1]
    handler('stage_changed', { projectId: 'project-1' })

    expect(onEvent).toHaveBeenCalledWith('stage_changed', { projectId: 'project-1' })

    unmount()
    expect(signalr.connection.stop).toHaveBeenCalled()
  })
})
