import { afterEach, describe, expect, it } from 'vitest'
import { act, fireEvent, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ProjectProvider } from '../../../entities/project'
import { TaskProgressPanel, type TaskProgressTimelineHook } from './TaskProgressPanel'
import type { TaskLogDataHook } from './TaskLogPanel'
import { WorkflowStage, type WorkflowTimeline } from '../../../entities/issue'

const timelineHook: TaskProgressTimelineHook = () => ({ data: timeline })
const taskLogHook: TaskLogDataHook = () => ({ data: { lines: [], nextCursor: null, truncated: false }, isLoading: false, isError: false })
const projects = [{ id: 'proj-1', name: 'Project 1', path: '/tmp/p1', createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', repositories: [] }]
const timeline: WorkflowTimeline = {
  workflowRunId: 'workflow-run-1', status: 'Completed', currentStage: WorkflowStage.Build, pendingWork: null, availableActions: [],
  stages: [{
    stage: WorkflowStage.Build, status: 'completed', order: 2, startedAt: '2026-01-01T00:00:00.000Z', completedAt: '2026-01-01T00:03:00.000Z', durationMs: 180000, checks: [], approval: null,
    tasks: [
      { id: 'process.1', title: 'Run release command', uses: 'core/process', status: 'completed', startedAt: '2026-01-01T00:00:00.000Z', completedAt: '2026-01-01T00:01:00.000Z', durationMs: 60000, attempts: 1, message: null, output: { stdout: 'release-ready', exitCode: 0 } },
      { id: 'open-pr.1', title: 'Open release PR', uses: 'mohist/create-pull-request', status: 'completed', startedAt: '2026-01-01T00:01:00.000Z', completedAt: '2026-01-01T00:02:00.000Z', durationMs: 60000, attempts: 1, message: null, output: { kind: 'create-pull-request', prNumber: 42, prUrl: 'https://example.test/pr/42', mergeCommitSha: null, targetBranch: 'main' } },
      { id: 'no-output.1', title: 'Complete without output', uses: 'core/process', status: 'completed', startedAt: '2026-01-01T00:02:00.000Z', completedAt: '2026-01-01T00:03:00.000Z', durationMs: 60000, attempts: 1, message: null, output: null },
    ],
  }],
}

afterEach(() => document.body.replaceChildren())

describe('TaskProgressPanel structured output', () => {
  it('renders process and PR fields without fabricating null output', async () => {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjects={projects} initialProjectId="proj-1">
          <TaskProgressPanel issueNumber={161} currentStage={WorkflowStage.Build} isAgentRunning={false} timelineHook={timelineHook} taskLogHook={taskLogHook} />
        </ProjectProvider>
      </QueryClientProvider>,
    )

    for (const title of ['Run release command', 'Open release PR', 'Complete without output']) {
      fireEvent.click(screen.getByText(title).closest('button')!)
    }
    await act(async () => { await Promise.resolve() })

    expect(screen.getByText(/"stdout": "release-ready"/)).toBeInTheDocument()
    expect(screen.getByText(/"exitCode": 0/)).toBeInTheDocument()
    expect(screen.getByText(/"prNumber": 42/)).toBeInTheDocument()
    expect(screen.getByText(/"mergeCommitSha": null/)).toBeInTheDocument()
    expect(container.querySelectorAll('pre')).toHaveLength(2)
  })
})
