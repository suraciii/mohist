import { describe, expect, it, vi } from 'vitest'
import { routeEvent } from './handle-event'
import { REVERSE_DNS_EVENT_TYPES } from '@/shared/lib/canonical-event-types'
import { issueArtifactKeys, issueDetailKeys, issueListKeys, issueWorkflowKeys } from '@/entities/issue/api/query-keys'

describe('routeEvent', () => {
  it.each([
    REVERSE_DNS_EVENT_TYPES.TaskStarted,
    REVERSE_DNS_EVENT_TYPES.TaskCompleted,
    REVERSE_DNS_EVENT_TYPES.TaskFailed,
    REVERSE_DNS_EVENT_TYPES.ArtifactRecorded,
  ])('invalidates the scoped issue resources for %s', (eventName) => {
    const invalidateQueries = vi.fn()

    routeEvent(eventName, { projectId: 'project-1', issueNumber: 42 }, {
      queryClient: { invalidateQueries } as never,
      setRebaseConflict: vi.fn(),
      viewedIssue: null,
      projectId: 'project-1',
      pathname: '/',
    })

    if (eventName === REVERSE_DNS_EVENT_TYPES.ArtifactRecorded) {
      expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: issueWorkflowKeys.root('project-1', 42) })
      expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: issueArtifactKeys.root('project-1', 42) })
      expect(invalidateQueries).not.toHaveBeenCalledWith({ queryKey: issueDetailKeys.detail('project-1', 42), exact: true })
      expect(invalidateQueries).not.toHaveBeenCalledWith({ queryKey: issueListKeys.project('project-1') })
    } else {
      expect(invalidateQueries).toHaveBeenCalledWith({
        queryKey: issueDetailKeys.detail('project-1', 42),
        exact: true,
      })
      expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: issueListKeys.project('project-1') })
      expect(invalidateQueries).toHaveBeenCalledWith({ queryKey: issueWorkflowKeys.root('project-1', 42) })
    }
  })

  // Issue 484 / D6: `session.followup_completed` / `session.followup_failed`
  // are deprecated and removed from the activity-invalidation set. The
  // activity-lifecycle signal that now invalidates `agent-activity` (so the
  // Activity feed reflects a follow-up winding the session back to idle) is
  // `session.activity`. This replaces the former follow-up-event assertions.
  it.each([
    'session.activity',
    'coder_session_status_changed',
  ])('invalidates agent activity for %s (activity lifecycle)', (eventName) => {
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
