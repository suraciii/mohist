import { createElement } from 'react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../entities/project'
import { REVERSE_DNS_EVENT_TYPES } from '../../shared/lib/canonical-event-types'
import { LiveTaskProvider, type EventsConnectionHook } from './LiveTaskProvider'
import { TEST_PROJECT } from './_liveTaskProviderTestUtils'

let eventsConnectionCalls: Parameters<EventsConnectionHook>[] = []
const eventsConnectionHook: EventsConnectionHook = (...args) => {
  eventsConnectionCalls.push(args)
  return { status: 'disconnected', connection: null, reconnectVersion: 0 }
}

beforeEach(() => {
  eventsConnectionCalls = []
})

describe('LiveTaskProvider AgentSession realtime routing', () => {
  it('invalidates only the addressed generic session after envelope lineage is normalized', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    render(createElement(
      QueryClientProvider,
      { client: queryClient },
      createElement(ProjectProvider, {
        initialProjectId: TEST_PROJECT.id,
        initialProjects: [TEST_PROJECT],
        children: createElement(LiveTaskProvider, {
          children: createElement('div'),
          eventsConnectionHook,
        }),
      }),
    ))

    act(() => {
      eventsConnectionCalls[0][1](REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound, {
        id: 'evt-session-1',
        type: REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound,
        source: '/mohist/agent-sessions/session-1',
        specVersion: '1.0',
        payload: { sessionId: 'wrong-session' },
        extensions: { projectid: TEST_PROJECT.id, sessionid: 'session-1', agentid: 'agent-1' },
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['unified-session', TEST_PROJECT.id, 'session-1'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['unified-session', TEST_PROJECT.id, 'session-1', 'transcript'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['agents', TEST_PROJECT.id, 'agent-1', 'sessions'] })
    expect(invalidateSpy).not.toHaveBeenCalledWith(expect.objectContaining({
      queryKey: ['unified-session', TEST_PROJECT.id, 'wrong-session'],
    }))
  })

  it('invalidates generic session caches for context-health events', () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries')
    render(createElement(QueryClientProvider, { client: queryClient }, createElement(ProjectProvider, {
      initialProjectId: TEST_PROJECT.id,
      initialProjects: [TEST_PROJECT],
      children: createElement(LiveTaskProvider, { children: createElement('div'), eventsConnectionHook }),
    })))

    act(() => {
      eventsConnectionCalls[0][1](REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated, {
        id: 'evt-context-1', type: REVERSE_DNS_EVENT_TYPES.AgentSessionContextHealthUpdated,
        source: '/mohist/agent-sessions/session-1', specVersion: '1.0', payload: {},
        extensions: { projectid: TEST_PROJECT.id, sessionid: 'session-1' },
      })
    })

    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['unified-session', TEST_PROJECT.id, 'session-1'] })
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['unified-session', TEST_PROJECT.id, 'session-1', 'transcript'] })
  })
})
