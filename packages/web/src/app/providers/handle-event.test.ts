import { describe, expect, it, vi } from 'vitest'
import { routeEvent } from './handle-event'
import { REVERSE_DNS_EVENT_TYPES } from '@/shared/lib/canonical-event-types'

describe('routeEvent', () => {
  it('invalidates generic session queries when a runtime binding changes', () => {
    const invalidateQueries = vi.fn()

    routeEvent(REVERSE_DNS_EVENT_TYPES.AgentSessionRuntimeBound, {}, {
      queryClient: { invalidateQueries } as never,
      setRebaseConflict: vi.fn(),
      viewedIssue: null,
      projectId: 'project-1',
      pathname: '/',
    })

    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-session'] })
  })
})
