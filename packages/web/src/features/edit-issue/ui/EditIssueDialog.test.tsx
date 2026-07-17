import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { EditIssueDialog } from './EditIssueDialog'
import { IssueHealth, IssueStatus, updateIssue, type Issue } from '@/entities/issue'

describe('EditIssueDialog', () => {
  it('saves issue edits through the issue project scope', async () => {
    const response = makeIssue()
    const updateCalls: Parameters<typeof updateIssue>[] = []
    const issueUpdater: typeof updateIssue = async (...args) => {
      updateCalls.push(args)
      return response
    }
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

    render(
      <QueryClientProvider client={queryClient}>
        <EditIssueDialog open onClose={vi.fn()} issue={makeIssue()} issueUpdater={issueUpdater} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByDisplayValue('Original title'), { target: { value: 'Updated title' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(updateCalls).toHaveLength(1))
    const [issueNumber, payload, projectId] = updateCalls[0]!
    expect(issueNumber).toBe(7)
    expect(projectId).toBe('proj_scoped')
    expect(payload).toEqual(expect.objectContaining({ title: 'Updated title' }))

    queryClient.clear()
  })
})

function makeIssue(): Issue {
  return {
    number: 7,
    title: 'Original title',
    body: 'Original body',
    status: IssueStatus.Backlog,
    health: IssueHealth.Active,
    projectId: 'proj_scoped',
    labels: {},
    isDraft: false,
    canStart: true,
    blocker: null,
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
  }
}
