// @vitest-environment jsdom
import '@testing-library/jest-dom'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../../entities/project'
import { ActivityPage } from './ActivityPage'

vi.mock('../../../entities/agent/api/client', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/agent/api/client')>()
  return {
    ...actual,
    getAgentActivity: vi.fn(async () => ({
      summary: { active: 0, waiting: 0, completed: 1, failed: 0, slots: { active: 0, max: 1 } },
      sessions: [
        {
          issueId: 'issue-1',
          issueNumber: 1,
          issueTitle: 'Usage issue',
          issueStage: 'check',
          issueStatus: null,
          sessionId: 'session-1',
          status: 'completed',
          model: null,
          taskDescription: null,
          createdAt: '2026-01-01T00:00:00Z',
          completedAt: null,
          lastActivityAt: '2026-01-01T00:00:00Z',
          currentWorkItem: null,
          taskProgress: null,
          lastActivity: null,
          failureReason: null,
          usage: { inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.18, costCurrency: 'USD' },
        },
      ],
      waiting: [],
    })),
  }
})

vi.mock('../../../widgets/runner-status', () => ({
  RunnerSummaryBadge: () => <div />,
  RunnerListCard: () => <div />,
}))

const TEST_PROJECT = {
  id: 'test-project',
  name: 'demo',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  repositories: [],
}

function renderActivityPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId={TEST_PROJECT.id} initialProjects={[TEST_PROJECT]}>
        <MemoryRouter>
          <ActivityPage />
        </MemoryRouter>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('ActivityPage usage snapshot', () => {
  afterEach(() => {
    vi.clearAllMocks()
  })

  it('renders activity-window usage totals and scope label', async () => {
    renderActivityPage()

    const snapshot = await screen.findByTestId('usage-snapshot-label')
    await waitFor(() => expect(within(snapshot).getByText('150 total tokens')).toBeInTheDocument())
    expect(within(snapshot).getByText('$0.18')).toBeInTheDocument()
    expect(within(snapshot).getByText('activity window only')).toBeInTheDocument()
  })
})
