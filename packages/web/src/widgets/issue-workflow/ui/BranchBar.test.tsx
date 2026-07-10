import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ComponentProps } from 'react'
import { http, HttpResponse } from 'msw'
import { useMswServer } from '../../../../tests/support/msw'
import { ProjectProvider } from '../../../entities/project'
import { WorkflowStage } from '../../../entities/issue'
import { BranchBar } from './BranchBar'

let rebaseRequests: string[] = []

useMswServer(
  http.get('*/api/projects/:projectId/issues/:issueNumber/workspace-status', () =>
    new Promise<never>(() => {})),
  http.post('*/api/projects/:projectId/issues/:issueNumber/rebase', ({ params }) => {
    rebaseRequests.push(String(params.issueNumber))
    return HttpResponse.json({
      success: true,
      data: { status: 'queued', message: 'Rebase task queued', rebased: false },
    })
  }),
)

const project = {
  id: 'proj-1',
  name: 'Project 1',
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-01T00:00:00.000Z',
  repositories: [],
}

function renderBranch(
  workspaceStatus: unknown | 'loading',
  props: Partial<ComponentProps<typeof BranchBar>> = {},
) {
  const issueNumber = props.issueNumber ?? 161
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
  })
  const queryKey = ['issues', issueNumber, project.id, 'workspace-status']
  queryClient.setQueryDefaults(queryKey, { staleTime: Number.POSITIVE_INFINITY })
  if (workspaceStatus !== 'loading') {
    queryClient.setQueryData(queryKey, workspaceStatus)
  }

  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjects={[project]} initialProjectId={project.id}>
        <BranchBar
          issueNumber={issueNumber}
          stage={props.stage === undefined ? WorkflowStage.Build : props.stage}
          isAgentRunning={props.isAgentRunning ?? false}
          baseBranch={props.baseBranch}
          allowRebase={props.allowRebase}
        />
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

beforeEach(() => {
  rebaseRequests = []
})

afterEach(() => {
  cleanup()
})

describe('BranchBar', () => {
  it('shows the rebase action whenever a workspace exists, even without a workflow stage', () => {
    renderBranch({
      exists: true,
      branch: 'mohist/run-wr-161',
      baseBranch: 'master',
      ahead: 11,
      behind: 80,
      rebaseInProgress: false,
      conflictingFiles: [],
    }, { stage: null, isAgentRunning: true, baseBranch: 'master', allowRebase: true })

    expect(screen.getByRole('button', { name: /rebase onto master/i })).not.toBeDisabled()
    expect(screen.getByText(/80 behind/i)).toBeTruthy()
  })

  it('shows a stable rebase action while workspace status is loading when a run exists', () => {
    renderBranch('loading', { stage: null, isAgentRunning: true, baseBranch: 'master', allowRebase: true })

    expect(screen.getByText('Checking upstream...')).toBeTruthy()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('does not claim ahead/behind status until the runner returns numeric counts', () => {
    renderBranch({
      exists: true,
      branch: 'mohist/run-wr-161',
      baseBranch: 'master',
    }, { baseBranch: 'master', allowRebase: true })

    expect(screen.getByText('Checking upstream...')).toBeTruthy()
    expect(screen.queryByText('workspace available')).toBeNull()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('does not treat error status default 0/0 counts as real workspace progress', () => {
    renderBranch({
      exists: false,
      reason: 'git_error',
      ahead: 0,
      behind: 0,
      rebaseInProgress: false,
      conflictingFiles: [],
    }, { baseBranch: 'master', allowRebase: true })

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.queryByText('up to date')).toBeNull()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('shows retained Done workflow workspace archive-removal copy when a Done issue has a workspace', () => {
    renderBranch({
      exists: true,
      branch: 'mo/issue-146',
      baseBranch: 'main',
      ahead: 0,
      behind: 0,
    }, { issueNumber: 146, stage: WorkflowStage.Done })

    expect(screen.getByText(/retained for review, traceability, diff inspection, and debugging/i)).toBeTruthy()
    expect(screen.getByText(/Archiving will remove the retained workspace/i)).toBeTruthy()
  })

  it('shows unknown upstream state without stale numbers or rebase action when fetch fails', () => {
    renderBranch({
      exists: true,
      reason: 'fetch_failed',
      branch: 'mohist/run-wr-216',
      baseBranch: 'main',
    }, { issueNumber: 216, stage: WorkflowStage.Check })

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.queryByText(/up to date/i)).toBeNull()
    expect(screen.queryByRole('button', { name: /rebase onto main/i })).toBeNull()
    expect(screen.queryByText(/behind/i)).toBeNull()
  })

  it('keeps the rebase action visible when upstream check fails for a workflow run', () => {
    renderBranch({
      exists: true,
      reason: 'fetch_failed',
      branch: 'mohist/run-wr-161',
      baseBranch: 'master',
      ahead: 0,
      behind: 0,
    }, { baseBranch: 'master', allowRebase: true })

    expect(screen.getByText('未能检查上游')).toBeTruthy()
    expect(screen.getByRole('button', { name: /rebase onto master/i })).toBeDisabled()
  })

  it('keeps rebasing state above unknown upstream state when fetch fails during rebase', () => {
    renderBranch({
      exists: true,
      reason: 'fetch_failed',
      branch: 'mohist/run-wr-216',
      baseBranch: 'main',
      rebaseInProgress: true,
      conflictingFiles: ['packages/runner/src/server/runner-signalr.ts'],
    }, { issueNumber: 216, stage: WorkflowStage.Check })

    expect(screen.getByText('Rebasing...')).toBeTruthy()
    expect(screen.getByText('packages/runner/src/server/runner-signalr.ts')).toBeTruthy()
    expect(screen.queryByText('未能检查上游')).toBeNull()
  })

  it('disables repeat rebase after a rebase task is queued', async () => {
    renderBranch({
      exists: true,
      branch: 'mohist/run-wr-161',
      baseBranch: 'master',
      ahead: 11,
      behind: 80,
      rebaseInProgress: false,
      conflictingFiles: [],
    }, { baseBranch: 'master', allowRebase: true })

    fireEvent.click(screen.getByRole('button', { name: /rebase onto master/i }))

    await waitFor(() => expect(screen.getByText('Rebase queued')).toBeTruthy())
    expect(screen.queryByRole('button', { name: /rebase onto master/i })).toBeNull()
    expect(rebaseRequests).toEqual(['161'])
  })
})
