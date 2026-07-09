// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest'

import { screen, waitFor } from '@testing-library/react'

import { EpicStatus, type EpicDetail } from '../../../entities/epic'
import { IssueStatus, WorkflowStage, IssueHealth } from '../../../entities/issue'

import { issues, linkedIssue, renderPage } from './_epicDetailPageTestHarness'
import { mountEpicDetail, mockEpic } from './_epicDetailMsw'

describe('EpicDetailPage current activity listing', () => {
  function makeEpic(overrides: Record<string, unknown> = {}): EpicDetail {
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
        totalIssueCount: 2,
        blockedIssues: [{ id: 'issue-2', number: 2, title: 'Blocked issue', health: 'blocked' }],
        activeIssues: [{ id: 'issue-1', number: 1, title: 'Active issue', health: 'active' }],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Active issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, priority: 'p1' }),
        linkedIssue({ id: 'issue-2', number: 2, title: 'Blocked issue', status: IssueStatus.InProgress, stage: WorkflowStage.Build, health: IssueHealth.Blocked, priority: 'p2' }),
      ],
      ...overrides,
    } as EpicDetail
  }

  mountEpicDetail(makeEpic(), issues)

  afterEach(() => {
    mockEpic(makeEpic())
  })

  it('lists concrete in-flight issues with number, title, and health coloring, and offers navigation', async () => {
    renderPage()

    const list = await screen.findByTestId('current-activity-list')
    expect(list.getAttribute('data-active-count')).toBe('1')
    expect(list.getAttribute('data-blocked-count')).toBe('1')

    const entries = await screen.findAllByTestId('current-activity-entry')
    expect(entries.length).toBe(2)

    const blocked = entries.find(entry => entry.getAttribute('data-health') === 'blocked')
    const active = entries.find(entry => entry.getAttribute('data-health') === 'active')
    expect(blocked).toBeTruthy()
    expect(active).toBeTruthy()

    expect(blocked?.textContent).toContain('#2')
    expect(blocked?.textContent).toContain('Blocked issue')
    expect(blocked?.getAttribute('href')).toContain('/issues/2')

    expect(active?.textContent).toContain('#1')
    expect(active?.textContent).toContain('Active issue')
    expect(active?.getAttribute('href')).toContain('/issues/1')

    expect(screen.queryByText(/0 blocked, 0 active/i)).toBeNull()
  })

  it('reflects real activity instead of a constant zero for both active and blocked counts', async () => {
    mockEpic(makeEpic({
      progress: {
        deliveredCount: 0,
        totalIssueCount: 3,
        blockedIssues: [
          { id: 'issue-3', number: 3, title: 'Stuck issue', health: 'blocked' },
        ],
        activeIssues: [
          { id: 'issue-1', number: 1, title: 'Active issue', health: 'active' },
          { id: 'issue-2', number: 2, title: 'Another active issue', health: 'active' },
        ],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: false,
      },
    }))

    renderPage()

    const list = await screen.findByTestId('current-activity-list')
    expect(list.getAttribute('data-active-count')).toBe('2')
    expect(list.getAttribute('data-blocked-count')).toBe('1')

    const entries = await screen.findAllByTestId('current-activity-entry')
    expect(entries.length).toBe(3)

    expect(screen.queryByText(/0 blocked, 0 active/i)).toBeNull()
  })

  it('shows an empty-state message when no active or blocked issues are in flight', async () => {
    mockEpic(makeEpic({
      progress: {
        deliveredCount: 1,
        totalIssueCount: 1,
        blockedIssues: [],
        activeIssues: [],
        nextIssue: null,
        nextIssueReason: null,
        readyToMarkDone: true,
      },
      linkedIssues: [
        linkedIssue({ id: 'issue-1', number: 1, title: 'Done issue', status: IssueStatus.Done, stage: WorkflowStage.Done, health: IssueHealth.Done, priority: 'p2' }),
      ],
    }))

    renderPage()

    await waitFor(() => {
      expect(screen.queryByTestId('current-activity-empty')).not.toBeNull()
    })

    const empty = screen.getByTestId('current-activity-empty')
    expect(empty.textContent).toMatch(/no current activity/i)
    expect(screen.queryByTestId('current-activity-list')).toBeNull()
    expect(screen.queryByTestId('current-activity-entry')).toBeNull()
  })
})
