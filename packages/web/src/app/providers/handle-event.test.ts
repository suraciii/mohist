import { describe, expect, it, vi } from 'vitest'
import { routeEvent } from './handle-event'
import { REVERSE_DNS_EVENT_TYPES } from '@/shared/lib/canonical-event-types'

describe('routeEvent', () => {
  it.each([
    REVERSE_DNS_EVENT_TYPES.TaskStarted,
    REVERSE_DNS_EVENT_TYPES.TaskCompleted,
    REVERSE_DNS_EVENT_TYPES.TaskFailed,
    REVERSE_DNS_EVENT_TYPES.ArtifactRecorded,
  ])('invalidates issue queries for %s', (eventName) => {
    const invalidateQueries = vi.fn()

    routeEvent(eventName, {}, {
      queryClient: { invalidateQueries } as never,
      setRebaseConflict: vi.fn(),
      viewedIssue: null,
      projectId: 'project-1',
      pathname: '/',
    })

    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['issues'] })
  })

  it.each([
    'session.followup_completed',
    'session.followup_failed',
  ])('invalidates agent activity for %s', (eventName) => {
    const invalidateQueries = vi.fn()

    routeEvent(eventName, {}, {
      queryClient: { invalidateQueries } as never,
      setRebaseConflict: vi.fn(),
      viewedIssue: null,
      projectId: 'project-1',
      pathname: '/',
    })

    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-activity'] })
  })

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
    expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: ['agent-sessions'] })
  })
})
