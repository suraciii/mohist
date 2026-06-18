// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

import { CreateIssueDialog } from './CreateIssueDialog'

const mocks = vi.hoisted(() => ({
  createIssue: vi.fn(),
  useLabels: vi.fn(() => ({ data: [] })),
  useProject: vi.fn(() => ({ projectId: 'proj_create', projects: [{ id: 'proj_create', name: 'Project' }] })),
  useRepositories: vi.fn(() => ({ data: [{ name: 'main', isDefault: true }] })),
  useAvailableModelIds: vi.fn(() => ({ data: [] })),
  useWorkflowProfiles: vi.fn(() => ({ data: [] })),
}))

vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  createIssue: mocks.createIssue,
  useLabels: mocks.useLabels,
}))

vi.mock('../../../entities/project', () => ({
  useProject: mocks.useProject,
  useRepositories: mocks.useRepositories,
}))

vi.mock('../../../entities/settings', () => ({
  useAvailableModelIds: mocks.useAvailableModelIds,
  useWorkflowProfiles: mocks.useWorkflowProfiles,
}))

describe('CreateIssueDialog', () => {
  afterEach(() => {
    cleanup()
    vi.clearAllMocks()
  })

  it('creates issue with attachment ids from the composer body', async () => {
    mocks.createIssue.mockResolvedValue({ id: 'issue_1', number: 1 })
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })

    render(
      <QueryClientProvider client={queryClient}>
        <CreateIssueDialog open onClose={vi.fn()} />
      </QueryClientProvider>,
    )

    fireEvent.change(screen.getByPlaceholderText('Issue title'), { target: { value: 'New issue' } })
    fireEvent.change(screen.getByPlaceholderText('Optional description'), { target: { value: 'See ![screen](att:att_created)' } })
    fireEvent.click(screen.getByRole('button', { name: 'Create' }))

    await waitFor(() => expect(mocks.createIssue).toHaveBeenCalledTimes(1))
    expect(mocks.createIssue).toHaveBeenCalledWith(expect.objectContaining({
      title: 'New issue',
      body: 'See ![screen](att:att_created)',
      attachmentIds: ['att_created'],
      projectId: 'proj_create',
    }))

    queryClient.clear()
  })
})
