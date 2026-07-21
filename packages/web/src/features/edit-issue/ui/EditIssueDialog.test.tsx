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

  it('shows only post-envelope description and preserves a closed CRLF envelope on save', async () => {
    const body = [
      '---',
      'recommended_workflow: mohist/github-pr',
      'unknown_key: keep this',
      'risk: medium',
      '---',
      'Original description',
    ].join('\r\n')
    const { updateCalls, queryClient } = renderDialog(makeIssue({ body }))

    const editor = screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement
    expect(editor.value).toBe('Original description')
    expect(editor.value).not.toContain('recommended_workflow')

    fireEvent.change(editor, { target: { value: 'Edited description with att:new_file' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(updateCalls).toHaveLength(1))
    expect(updateCalls[0]![1]).toEqual(expect.objectContaining({
      body: [
        '---',
        'recommended_workflow: mohist/github-pr',
        'unknown_key: keep this',
        'risk: medium',
        '---',
        'Edited description with att:new_file',
      ].join('\r\n'),
      attachmentIds: ['new_file'],
    }))
    queryClient.clear()
  })

  it('hides and repairs an unclosed envelope with exactly one closing delimiter', async () => {
    const body = ['---', 'risk: medium'].join('\n')
    const { updateCalls, queryClient } = renderDialog(makeIssue({ body }))

    const editor = screen.getByPlaceholderText('Optional description') as HTMLTextAreaElement
    expect(editor.value).toBe('')
    fireEvent.change(editor, { target: { value: 'New visible description' } })
    fireEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(updateCalls).toHaveLength(1))
    expect(updateCalls[0]![1].body).toBe([
      '---',
      'risk: medium',
      '---',
      'New visible description',
    ].join('\n'))
    queryClient.clear()
  })
})

function renderDialog(issue: Issue) {
  const updateCalls: Parameters<typeof updateIssue>[] = []
  const issueUpdater: typeof updateIssue = async (...args) => {
    updateCalls.push(args)
    return issue
  }
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  render(
    <QueryClientProvider client={queryClient}>
      <EditIssueDialog open onClose={vi.fn()} issue={issue} issueUpdater={issueUpdater} />
    </QueryClientProvider>,
  )
  return { updateCalls, queryClient }
}

function makeIssue(overrides: Partial<Issue> = {}): Issue {
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
    ...overrides,
  }
}
