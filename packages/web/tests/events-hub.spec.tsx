import { renderHook } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { createEventsConnection, useEventsConnection } from '../src/shared/api/events-hub'
import { fakeConnections, lastBuilderChain, LogLevel } from '../tests/support/signalr-fake'

describe('events hub SignalR client', () => {
  it('connects to the project-scoped SignalR hub', () => {
    createEventsConnection('proj/with space')

    expect(lastBuilderChain.withUrl).toHaveBeenCalledWith('/hubs/events?projectId=proj%2Fwith%20space')
    expect(lastBuilderChain.withAutomaticReconnect).toHaveBeenCalled()
    expect(lastBuilderChain.configureLogging).toHaveBeenCalledWith(LogLevel.Warning)
    expect(lastBuilderChain.build).toHaveBeenCalled()
  })

  it('routes OnEvent messages to the supplied callback and stops on cleanup', async () => {
    const onEvent = vi.fn()
    const { unmount } = renderHook(() => useEventsConnection('project-1', onEvent))

    const connection = fakeConnections[fakeConnections.length - 1]
    expect(connection.on).toHaveBeenCalledWith('OnEvent', expect.any(Function))
    expect(connection.start).toHaveBeenCalled()

    const handler = vi.mocked(connection.on).mock.calls[0][1]
    handler('stage_changed', { projectId: 'project-1' })

    expect(onEvent).toHaveBeenCalledWith('stage_changed', { projectId: 'project-1' })

    unmount()
    expect(connection.stop).toHaveBeenCalled()
  })
})
