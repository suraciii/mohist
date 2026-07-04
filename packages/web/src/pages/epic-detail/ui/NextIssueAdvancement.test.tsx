// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, screen } from '@testing-library/react'

import { EpicStatus } from '../../../entities/epic'
import { IssueStatus, WorkflowStage } from '../../../entities/issue'

import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'

/**
 * Tests for the Next Issue / advancement-copy region rendered inside <EpicDetailPage/>.
 */

const mocks = vi.hoisted(() => ({
  useProject: vi.fn(),
  useEpic: vi.fn(),
  useIssues: vi.fn(),
  useAddEpicIssue: vi.fn(),
  useRemoveEpicIssue: vi.fn(),
  useStartIssue: vi.fn(),
  useStartEpic: vi.fn(),
  useMarkEpicDone: vi.fn(),
  useCloseEpic: vi.fn(),
  useUpdateEpic: vi.fn(),
  usePauseEpic: vi.fn(),
  useResumeEpic: vi.fn(),
}))

vi.mock('../../../entities/project', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/project')>()
  return {
    ...actual,
    useProject: mocks.useProject,
  }
})
vi.mock('../../../entities/issue', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../../../entities/issue')>()),
  useIssues: mocks.useIssues,
}))

vi.mock('../../../entities/epic', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../entities/epic')>()
  return {
    ...actual,
    useEpic: mocks.useEpic,
    useAddEpicIssue: mocks.useAddEpicIssue,
    useRemoveEpicIssue: mocks.useRemoveEpicIssue,
    useStartIssue: mocks.useStartIssue,
    useStartEpic: mocks.useStartEpic,
    useMarkEpicDone: mocks.useMarkEpicDone,
    useCloseEpic: mocks.useCloseEpic,
    useUpdateEpic: mocks.useUpdateEpic,
    usePauseEpic: mocks.usePauseEpic,
    useResumeEpic: mocks.useResumeEpic,
  }
})

vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal<typeof import('react-router-dom')>()
  return {
    ...actual,
    useNavigate: () => vi.fn(),
  }
})

describe('EpicDetailPage next issue reason display', () => {
  const addMutate = vi.fn()
  const removeMutate = vi.fn()
  const startMutate = vi.fn()
  const doneMutate = vi.fn()
  const closeMutate = vi.fn()
  const updateMutate = vi.fn()
  const pauseMutate = vi.fn()
  const resumeMutate = vi.fn()
  const startEpicMutate = vi.fn()

  function makeEpic(overrides: Record<string, unknown> = {}) {
    return {
      id: 'epic-12345678',
      number: null,
      title: 'Epic title',
      description: 'Epic description',
      priority: 'p1',
      status: EpicStatus.Idle,
      createdAt: '2026-01-01T00:00:00Z',
      updatedAt: '2026-01-01T00:00:00Z',
      progress: {
        deliveredCount: 0,
        totalIssueCount: 1,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: 'Waiting on #5',
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Pending issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
      ],
      ...overrides,
    }
  }

  beforeEach(() => {
    vi.clearAllMocks()
    mocks.useProject.mockReturnValue({ projectId: 'proj-1' })
    mocks.useIssues.mockReturnValue({ data: issues })
    mocks.useAddEpicIssue.mockReturnValue({ mutate: addMutate, isPending: false, isError: false })
    mocks.useRemoveEpicIssue.mockReturnValue({ mutate: removeMutate, isPending: false, isError: false })
    mocks.useStartIssue.mockReturnValue({ mutate: startMutate, isPending: false, isError: false })
    mocks.useMarkEpicDone.mockReturnValue({ mutate: doneMutate, isPending: false })
    mocks.useCloseEpic.mockReturnValue({ mutate: closeMutate, isPending: false })
    mocks.useUpdateEpic.mockReturnValue({ mutate: updateMutate, isPending: false, isError: false })
    mocks.usePauseEpic.mockReturnValue({ mutate: pauseMutate, isPending: false })
    mocks.useResumeEpic.mockReturnValue({ mutate: resumeMutate, isPending: false })
    mocks.useStartEpic.mockReturnValue({ mutate: startEpicMutate, isPending: false })
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the advancement-state copy (waiting-for-in-progress with nav link) when nextIssue is null', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })

    renderPage()

    const copy = screen.getByTestId('advancement-copy')
    expect(copy.textContent).toContain('Waiting for #1 to finish')
    const link = screen.getByTestId('advancement-link')
    expect(link.getAttribute('href')).toContain('/issues/1')
    expect(screen.queryByTestId('mark-epic-done')).toBeTruthy()
  })

  it('shows the next issue link without a Start button when a next issue exists', () => {
    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 0,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: { id: 'issue-3', number: 3, title: 'Candidate issue' },
          nextIssueReason: null,
          readyToMarkDone: false,
        },
        linkedIssues: [linkedIssue({ id: 'issue-3', number: 3, title: 'Candidate issue' })],
      }),
      isLoading: false,
    })

    renderPage()

    expect(screen.getByRole('link', { name: '#3 Candidate issue' }).getAttribute('href')).toContain('/issues/3')
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    expect(startMutate).not.toHaveBeenCalled()
  })

  it('does not show a Next Issue Start action for reason ready or empty states', () => {
    mocks.useEpic.mockReturnValue({ data: makeEpic(), isLoading: false })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 1,
          totalIssueCount: 1,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: true,
        },
      }),
      isLoading: false,
    })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

    mocks.useEpic.mockReturnValue({
      data: makeEpic({
        progress: {
          deliveredCount: 0,
          totalIssueCount: 0,
          blockedIssues: [],
          activeIssues: [],
          nextIssue: null,
          nextIssueReason: null,
          readyToMarkDone: false,
        },
      }),
      isLoading: false,
    })
    renderPage()
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
  })
})
