import { describe, expect, it, vi } from 'vitest'
import { REVERSE_DNS_EVENT_TYPES } from '../../../shared/lib/canonical-event-types'
import { issueArtifactKeys, issueCandidateKeys, issueDetailKeys, issueListKeys, issueWorkflowKeys } from './query-keys'
import { invalidateIssueEvent } from './invalidation'

function makeClient() {
  return { invalidateQueries: vi.fn() }
}

describe('issue event invalidation mapping', () => {
  it('does not invalidate viewed issue 473 for a workflow event from issue 474', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunFailed,
      { projectId: 'project-1', issueNumber: 474 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 474),
      exact: true,
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.root('project-1', 474),
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueListKeys.project('project-1'),
    })
    expect(queryClient.invalidateQueries).not.toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 473),
      exact: true,
    })
  })

  it('invalidates issue status resources for blocked Agent-result attention', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.WorkflowRunBlocked,
      { projectId: 'project-1', issueNumber: 474 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 474),
      exact: true,
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.root('project-1', 474),
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueListKeys.project('project-1'),
    })
  })

  it('does not target issue detail for an inbox hint carrying an issue number', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.InboxItemPersisted,
      { projectId: 'project-1', issueNumber: 473, kind: 'workflow_failed' },
      'project-1',
    )

    expect(queryClient.invalidateQueries).not.toHaveBeenCalled()
  })

  it('keeps reverse-DNS artifact and workflow resources scoped to the event issue', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.ArtifactRecorded,
      { projectId: 'project-1', issueNumber: 474 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.root('project-1', 474),
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueArtifactKeys.root('project-1', 474),
    })
    expect(queryClient.invalidateQueries).not.toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 474),
      exact: true,
    })
  })

  it('ignores events without the current project identity', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.IssueCompleted,
      { issueNumber: 473 },
      'project-1',
    )
    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.IssueCompleted,
      { projectId: 'project-2', issueNumber: 473 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).not.toHaveBeenCalled()
  })

  it.each([
    REVERSE_DNS_EVENT_TYPES.IssueDraftChanged,
    REVERSE_DNS_EVENT_TYPES.IssueRepositoryChanged,
    REVERSE_DNS_EVENT_TYPES.IssueCompositeStarted,
    REVERSE_DNS_EVENT_TYPES.IssueCompositeStatusChanged,
  ])('invalidates detail and list for server-produced structural event %s', (eventName) => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      eventName,
      { projectId: 'project-1', issueNumber: 473 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueDetailKeys.detail('project-1', 473),
      exact: true,
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueListKeys.project('project-1'),
    })
  })

  it('invalidates the child, both parent details, list, and candidates when parent changes', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.IssueParentChanged,
      {
        projectId: 'project-1',
        issueNumber: 473,
        previousParentIssueNumber: 470,
        parentIssueNumber: 471,
      },
      'project-1',
    )

    for (const issueNumber of [473, 470, 471]) {
      expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
        queryKey: issueDetailKeys.detail('project-1', issueNumber),
        exact: true,
      })
    }
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueListKeys.project('project-1'),
    })
    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueCandidateKeys.project('project-1'),
      exact: true,
    })
  })

  it('invalidates workflow resources when the effective profile changes', () => {
    const queryClient = makeClient()

    invalidateIssueEvent(
      queryClient,
      REVERSE_DNS_EVENT_TYPES.IssueWorkflowProfileChanged,
      { projectId: 'project-1', issueNumber: 473 },
      'project-1',
    )

    expect(queryClient.invalidateQueries).toHaveBeenCalledWith({
      queryKey: issueWorkflowKeys.root('project-1', 473),
    })
  })
})
