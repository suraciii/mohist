// @vitest-environment jsdom
import { describe, expect, it, vi } from 'vitest'
import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { http, HttpResponse } from 'msw'

import { EditIssueDialog } from './EditIssueDialog'
import { IssueHealth, IssueStatus, type Issue } from '@/entities/issue'
import { useMswServer } from '../../../../tests/support/msw'

let _updateResponse: Issue | null = null

const updateHandler = vi.fn(async (info: { request: Request }) => {
  const body = await info.request.clone().json()
  void body
  const data = _updateResponse ?? makeIssue()
  return HttpResponse.json({ success: true, data })
})

useMswServer(
  http.patch('*/api/projects/:projectId/issues/:issueId', updateHandler),
)

describe('EditIssueDialog', () => {
  it('saves issue edits through the issue project scope', async () => {
    const response = makeIssue()
    _updateResponse = response
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

    render(
      <QueryClientProvider client={queryClient}>
        <EditIssueDialog open onClose={vi.fn()} issue={makeIssue()} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByDisplayValue('Original title'), { target: { value: 'Updated title' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(updateHandler).toHaveBeenCalledTimes(1))
    const call = updateHandler.mock.calls[0]![0]
    const url = new URL(call.request.url)
    expect(url.pathname).toContain('/issues/7')
    const callBody = await call.request.clone().json()
    expect(callBody).toEqual(expect.objectContaining({ title: 'Updated title' }))

    queryClient.clear()
  })
})

function makeIssue(): Issue {
  return {
    id: 'issue_7',
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
