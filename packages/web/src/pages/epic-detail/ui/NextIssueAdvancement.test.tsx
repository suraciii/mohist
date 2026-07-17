import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import { cleanup, screen } from '@testing-library/react'

import { EpicStatus, type EpicDetail } from '../../../entities/epic'
import { IssueStatus, WorkflowStage } from '../../../entities/issue'

import { issues, linkedIssue, renderPage } from './_epicDetailPageTestUtils'

let _epicData: EpicDetail

describe('EpicDetailPage next issue reason display', () => {
  function makeEpic(overrides: Record<string, unknown> = {}): EpicDetail {
    return {
      projectId: 'proj-1',
      number: 7,
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
        linkedIssue({ number: 1, title: 'Pending issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
      ],
      ...overrides,
    } as EpicDetail
  }

  beforeEach(() => {
    vi.clearAllMocks()
    _epicData = makeEpic()
  })

  afterEach(() => {
    cleanup()
  })

  it('shows the advancement-state copy (waiting-for-in-progress with nav link) when nextIssue is null', async () => {
    _epicData = makeEpic()

    renderPage({ epic: _epicData, issues })

    expect(await screen.findByTestId('advancement-copy')).toHaveTextContent('Waiting for #1 to finish')
    const link = screen.getByTestId('advancement-link')
    expect(link.getAttribute('href')).toContain('/issues/1')
    expect(screen.queryByTestId('mark-epic-done')).toBeTruthy()
  })

  it('shows the next issue link without a Start button when a next issue exists', async () => {
    _epicData = makeEpic({
      progress: {
        deliveredCount: 0,
        totalIssueCount: 1,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: { number: 3, title: 'Candidate issue' },
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [linkedIssue({ number: 3, title: 'Candidate issue' })],
    })

    renderPage({ epic: _epicData, issues })

    expect(await screen.findByRole('link', { name: '#3 Candidate issue' })).toHaveAttribute('href', expect.stringContaining('/issues/3'))
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
  })

  it('does not show a Next Issue Start action for reason ready or empty states', async () => {
    _epicData = makeEpic()
    renderPage({ epic: _epicData, issues })
    await screen.findByTestId('advancement-copy')
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

    _epicData = makeEpic({
      progress: {
        deliveredCount: 1,
        totalIssueCount: 1,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: true,
      },
    })
    renderPage({ epic: _epicData, issues })
    await screen.findByTestId('mark-epic-done')
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
    cleanup()

    _epicData = makeEpic({
      progress: {
        deliveredCount: 0,
        totalIssueCount: 0,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
    })
    renderPage({ epic: _epicData, issues })
    await screen.findByTestId('advancement-copy')
    expect(screen.queryByTestId('epic-detail-next-start')).toBeNull()
  })
})
