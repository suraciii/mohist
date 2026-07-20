import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { IssueHealth, IssueStatus, type Issue } from '../../../../entities/issue'
import type { AgentStatus } from '../../../../entities/agent'
import type { IssueDetailMutations } from '../../model/useIssueDetailMutations'
import { IssueActionsCard } from './IssueActionsCard'

function mutation() {
  return { mutate: vi.fn(), isPending: false } as unknown as IssueDetailMutations['markDoneMutation']
}

function mutations() {
  const value = mutation()
  return {
    approveMutation: value,
    sendBackMutation: value,
    startMutation: value,
    markReadyMutation: value,
    markDoneMutation: mutation(),
    closeMutation: value,
    resumeMutation: value,
    retryMutation: value,
    rerunMutation: value,
  } as unknown as Parameters<typeof IssueActionsCard>[0]['mutations']
}

function issue(overrides: Partial<Issue> = {}): Issue {
  return {
    number: 410,
    title: 'Delivered outside workflow',
    status: IssueStatus.InProgress,
    workflowStatus: 'stopped',
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-07-20T00:00:00Z',
    updatedAt: '2026-07-20T00:00:00Z',
    isDraft: false,
    canStart: false,
    blocker: null,
    ...overrides,
  }
}

function renderCard(currentIssue: Issue, currentMutations = mutations()) {
  render(
    <IssueActionsCard
      issue={currentIssue}
      agentStatus={{ activeAgents: [] } as unknown as AgentStatus}
      mutations={currentMutations}
      onAskAgent={vi.fn()}
    />,
  )
  return currentMutations
}

afterEach(cleanup)

describe('IssueActionsCard manual completion', () => {
  it('marks a stopped leaf issue done', () => {
    const currentMutations = renderCard(issue())

    fireEvent.click(screen.getByTestId('mark-issue-done'))

    expect(currentMutations.markDoneMutation.mutate).toHaveBeenCalledOnce()
  })

  it.each([
    ['running workflow', issue({ workflowStatus: 'running' })],
    ['failed workflow', issue({ workflowStatus: 'failed' })],
    ['parent issue', issue({ childIssuesSummary: { hasChildren: true, count: 1, backlogCount: 0, inProgressCount: 0, doneCount: 1, cancelledCount: 0, blockedCount: 0 } })],
    ['done issue', issue({ status: IssueStatus.Done, health: IssueHealth.Done, workflowStatus: 'completed' })],
  ])('hides the command for a %s', (_, currentIssue) => {
    renderCard(currentIssue)

    expect(screen.queryByTestId('mark-issue-done')).toBeNull()
  })
})
