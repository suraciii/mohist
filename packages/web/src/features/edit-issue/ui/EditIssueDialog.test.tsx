// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { EditIssueDialog } from './EditIssueDialog'
import { IssueHealth, IssueStatus, type Issue } from '@/entities/issue'

const mocks = vi.hoisted(() => ({
  updateIssue: vi.fn(),
  useLabels: vi.fn(() => ({ data: [] })),
}))

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  updateIssue: mocks.updateIssue,
  useLabels: mocks.useLabels,
}))

describe('EditIssueDialog', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('saves issue edits through the issue project scope', async () => {
    mocks.updateIssue.mockResolvedValue(makeIssue())
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

    render(
      <QueryClientProvider client={queryClient}>
        <EditIssueDialog open onClose={vi.fn()} issue={makeIssue()} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByDisplayValue('Original title'), { target: { value: 'Updated title' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(mocks.updateIssue).toHaveBeenCalledTimes(1))
    expect(mocks.updateIssue).toHaveBeenCalledWith(
      7,
      expect.objectContaining({ title: 'Updated title' }),
      'proj_scoped',
    )

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
    labels: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
  }
}
