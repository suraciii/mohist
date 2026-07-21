import { afterEach, describe, expect, it } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { ProjectProvider } from '../../../entities/project'
import type { Project } from '../../../entities/project'
import { IssueStatus, IssueHealth } from '../../../entities/issue'
import { IssueDetailPage } from './IssueDetailPage'
import { mockIssue, mountIssueDetail } from './_issueDetailMsw'

const projects: Project[] = [
  {
    id: 'proj-1',
    name: 'Project 1',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    repositories: [],
  },
]

function makeIssue(overrides: Record<string, unknown> = {}) {
  return {
    number: 14,
    title: 'Status uniqueness test',
    body: 'Body content.',
    status: IssueStatus.InProgress,
    health: IssueHealth.Active,
    projectId: 'proj-1',
    labels: {},
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    comments: [],
    isDraft: false,
    canStart: true,
    blocker: null,
    ...overrides,
  }
}

mountIssueDetail({ issue: makeIssue() })

function renderPage(initialEntry: string = '/issues/14') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[initialEntry]}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <Routes>
            <Route path="/issues/:number" element={<IssueDetailPage />} />
            <Route path="/:projectName/issues/:number" element={<IssueDetailPage />} />
          </Routes>
        </ProjectProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

afterEach(() => {
  cleanup()
})

describe('IssueDetailPage status uniqueness — running summary', () => {
  it('renders exactly one runtime summary in the headline and no header runtime pill', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'running',
      health: IssueHealth.Active,
      workflowStageProgress: {
        stage: 'build',
        total: 5,
        completed: 2,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Implement decision surface',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Implement decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('running')
    expect(headline.dataset.hasCurrentTask).toBe('true')

    const summary = within(headline).getByTestId('status-headline-summary')
    expect(summary.textContent?.toLowerCase()).toContain('running')

    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
    expect(page.querySelector('[data-testid="status-badges-runtime"]')).toBeNull()

    const surface = screen.getByTestId('issue-decision-surface')
    expect(within(surface).queryByTestId('status-headline-current-task')).toBeNull()
    expect(within(surface).queryByTestId('status-headline-summary')).toBeNull()
  })

  it('embeds the current task into the headline text and never renders a separate current-task pill', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'running',
      health: IssueHealth.Active,
      workflowStageProgress: {
        stage: 'build',
        total: 5,
        completed: 2,
        running: 1,
        failed: 0,
        currentTaskTitle: 'Implement decision surface',
      },
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Implement decision surface' },
        latestAttemptState: 'running',
        workflowSummaryState: 'running',
        allowedActions: ['stop'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    const headlineText = headline.textContent ?? ''
    expect(headlineText).toContain('Implement decision surface')
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()
  })
})

describe('IssueDetailPage status uniqueness — queued summary', () => {
  it('renders the queued summary once in the headline without a stage or current task when no progress exists', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.Backlog,
      workflowStage: null,
      workflowStatus: null,
      workflowRunId: null,
      health: IssueHealth.Active,
      blocker: { kind: 'waiting-for', issue: { number: 9, title: 'Prereq' } },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('queued')
    expect(headline.dataset.hasCurrentTask).toBe('false')
    expect(within(headline).queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()

    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })
})

describe('IssueDetailPage status uniqueness — approval-required summary', () => {
  it('renders the approval-required summary once in the headline and uses product-language copy that does not assume the viewer is the approver', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'check',
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('approval-required')
    const rationale = screen.getByTestId('decision-rationale').textContent ?? ''
    expect(rationale).toMatch(/approval.*pending|pending.*approval/i)
    expect(rationale).not.toMatch(/your review|review and approve/i)

    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })
})

describe('IssueDetailPage status uniqueness — blocked summary', () => {
  it('renders the blocked summary once in the headline without header runtime badge duplication', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'failed',
      health: IssueHealth.Blocked,
      blockedReason: 'Manual stop requested.',
      recovery: null,
      convergence: null,
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('blocked')
    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })

  it('identifies a manually stopped / interrupted workflow as a stop/recovery situation, not awaiting approval', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'interrupted',
      health: IssueHealth.Blocked,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'interrupted',
        allowedActions: ['retry', 'resume', 'rerun', 'stop'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const headline = screen.getByTestId('status-headline')
    expect(headline.dataset.summary).toBe('blocked')
    const rationale = screen.getByTestId('decision-rationale').textContent ?? ''
    expect(rationale).toMatch(/stopped manually|resume or rerun/i)
    expect(rationale).not.toMatch(/awaiting approval|pending review|your review/i)
  })
})

describe('IssueDetailPage status uniqueness — failed summary', () => {
  it('renders the failed summary once in the headline without a separate runtime badge row', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'failed',
      health: IssueHealth.Active,
      recovery: {
        currentWorkItem: { type: 'task', id: 't1', title: 'Build something' },
        latestAttemptState: 'failed',
        workflowSummaryState: 'waiting-for-recovery',
        allowedActions: ['retry', 'rerun', 'start'],
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('failed')
    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })
})

describe('IssueDetailPage status uniqueness — done summary', () => {
  it('renders the done summary once in the headline for an archived done issue', async () => {
    mockIssue(makeIssue({
      number: 264,
      status: IssueStatus.Done,
      workflowStage: 'done',
      workflowStatus: 'completed',
      archivedAt: '2026-06-25T10:00:00Z',
      health: IssueHealth.Done,
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('done')
    expect(headline.textContent?.toLowerCase()).toContain('done')
    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })
})

describe('IssueDetailPage composite parent issue-only status', () => {
  it('renders one issue-only status statement and no workflow stage or current task', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr_composite_1',
      health: IssueHealth.Active,
      children: [
        { number: 12, title: 'Server refactor', status: IssueStatus.Done, health: IssueHealth.Done, repositoryName: 'server' },
        { number: 13, title: 'Web portal upgrade', status: IssueStatus.InProgress, health: IssueHealth.Blocked, repositoryName: 'web' },
      ],
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 1,
      },
    }))

    renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('issue-only')
    expect(within(headline).queryByTestId('status-headline-stage-progress')).toBeNull()
    expect(screen.queryByTestId('status-headline-current-task')).toBeNull()
    expect(headline.textContent ?? '').toMatch(/1 of 2 child issues done|1 of 2.*done/i)

    const page = screen.getByTestId('issue-detail-page-container')
    expect(page.querySelectorAll('[data-testid="runtime-status-pill"]')).toHaveLength(0)
  })

  it('does not fetch workflow data or render workflow controls for a composite parent', async () => {
    mockIssue(makeIssue({
      number: 14,
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'running',
      workflowRunId: 'wr_composite_2',
      health: IssueHealth.Active,
      children: [
        { number: 12, title: 'Server refactor', status: IssueStatus.Done, health: IssueHealth.Done, repositoryName: 'server' },
      ],
      childIssuesSummary: {
        hasChildren: true,
        count: 1,
        backlogCount: 0,
        inProgressCount: 0,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 0,
      },
    }))

    const { container } = renderPage()

    const headline = await waitFor(() => screen.getByTestId('status-headline'))
    expect(headline.dataset.summary).toBe('issue-only')
    expect(container.querySelector('[data-testid="workflow-view-frame"]')).toBeNull()
  })
})

describe('IssueDetailPage IssueDetailsCard status uniqueness', () => {
  it('does not render Issue Stage or Workflow Stage rows in the Details card', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'check',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
      projectName: 'mohist-local',
    }))

    renderPage()

    const detailsMetadata = await waitFor(() => screen.getByTestId('issue-detail-details-metadata'))
    expect(within(detailsMetadata).queryByText('Issue Stage')).toBeNull()
    expect(within(detailsMetadata).queryByText('Workflow Stage')).toBeNull()
  })

  it('still renders parent/child relationships, project, repository name, base branch, and Git URL', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'check',
      parentIssueRef: { number: 13, title: 'Parent issue' },
      childIssuesSummary: {
        hasChildren: true,
        count: 2,
        backlogCount: 0,
        inProgressCount: 1,
        doneCount: 1,
        cancelledCount: 0,
        blockedCount: 0,
      },
      projectName: 'mohist-local',
      repository: {
        name: 'master',
        baseBranch: 'master',
        gitUrl: 'https://github.com/suraciii/mohist.git',
      },
    }))

    renderPage()

    const detailsMetadata = await waitFor(() => screen.getByTestId('issue-detail-details-metadata'))
    expect(within(detailsMetadata).getByTestId('parent-issue-metadata-row')).toBeTruthy()
    expect(within(detailsMetadata).getByTestId('child-issues-metadata-row')).toBeTruthy()
    expect(within(detailsMetadata).getByTestId('repository-metadata-row')).toBeTruthy()
    expect(within(detailsMetadata).getByText('mohist-local')).toBeTruthy()
    expect(within(detailsMetadata).getByTestId('repository-name')).toHaveTextContent('master')
    expect(within(detailsMetadata).getByTestId('repository-base-branch')).toHaveTextContent('master')
    expect(within(detailsMetadata).getByTestId('repository-git-url')).toHaveTextContent('https://github.com/suraciii/mohist.git')
  })
})

describe('IssueDetailPage two paused meanings are distinguishable', () => {
  it('uses approval-pending language for an approval pause, not stop/recovery language', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'check',
      health: IssueHealth.Paused,
      approvalState: {
        status: 'awaiting',
        stage: 'check',
        requestedAt: '2026-01-01T00:00:00.000Z',
      },
      recovery: {
        currentWorkItem: null,
        latestAttemptState: null,
        workflowSummaryState: 'awaiting-approval',
        allowedActions: ['approve', 'reject'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const rationale = screen.getByTestId('decision-rationale').textContent ?? ''
    expect(rationale).toMatch(/approval.*pending|pending.*approval/i)
    expect(rationale).not.toMatch(/stopped manually|interrupted/i)
  })

  it('uses stop/recovery language for a manually stopped workflow, not approval-pending language', async () => {
    mockIssue(makeIssue({
      status: IssueStatus.InProgress,
      workflowStage: 'build',
      workflowStatus: 'interrupted',
      health: IssueHealth.Blocked,
      recovery: {
        currentWorkItem: null,
        latestAttemptState: 'interrupted',
        workflowSummaryState: 'interrupted',
        allowedActions: ['retry', 'resume', 'rerun', 'stop'],
      },
    }))

    renderPage()

    await waitFor(() => screen.getByTestId('issue-decision-surface'))
    const rationale = screen.getByTestId('decision-rationale').textContent ?? ''
    expect(rationale).toMatch(/stopped manually|resume or rerun/i)
    expect(rationale).not.toMatch(/approval.*pending|awaiting.*review/i)
  })
})
