import { beforeEach, describe, expect, it } from 'vitest'
import { QueryClient, useMutation } from '@tanstack/react-query'
import { act, fireEvent, screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { createQueryClient, render } from '../../../../tests/test-utils'
import { useMswServer } from '../../../../tests/support/msw'
import { WorkflowView } from './WorkflowView'
import type { ArtifactContentHook } from './ArtifactContentViewer'
import type { StepListDependencies } from './InlineApproval'
import { IssueHealth, IssueStatus, WorkflowStage, type ApprovalFeedback, type Issue, type WorkflowTimeline } from '../../../entities/issue'
import type { TaskLogDataHook, WorkflowRunSessionsHook } from './TaskLogPanel'

let timeline: WorkflowTimeline

useMswServer(http.get('*/api/projects/:projectId/issues/:issueNumber/workflow/status', () => HttpResponse.json({ success: true, data: { workflow: timeline } })))

const dependencies: StepListDependencies = {
  approveIssue: async (issueNumber) => ({ issue: issue(issueNumber), context: null, message: 'approved' }),
  requestChangesHook: () => useMutation<ApprovalFeedback, Error, { issueNumber: number, data: { stage: string, body: string } }>({ mutationFn: async () => { throw new Error('not used') } }),
  artifactContentHook: (() => ({ data: undefined, isLoading: false, error: null })) as ArtifactContentHook,
  taskLogHook: (() => ({ data: { lines: [], nextCursor: null, truncated: false }, isLoading: false, isError: false })) as TaskLogDataHook,
  workflowSessionsHook: (() => ({ sessions: [], isLoading: false })) as WorkflowRunSessionsHook,
}

function issue(number = 1): Issue {
  return { number, title: 'Structured output', body: '', status: IssueStatus.InProgress, workflowStage: WorkflowStage.Build, health: IssueHealth.Active, projectId: 'test-project', labels: {}, createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-01T00:00:00.000Z', comments: [], isDraft: false, canStart: true, blocker: null }
}

function structuredTimeline(): WorkflowTimeline {
  const base = { status: 'completed' as const, startedAt: '2026-01-01T00:00:00.000Z', durationMs: 60000, attempts: 1, message: null }
  return {
    workflowRunId: 'workflow-run-1', status: 'Completed', currentStage: WorkflowStage.Build, pendingWork: null, availableActions: [],
    stages: [{
      stage: WorkflowStage.Build, status: 'completed', order: 2, startedAt: '2026-01-01T00:00:00.000Z', completedAt: '2026-01-01T00:03:00.000Z', durationMs: 180000, checks: [], approval: null,
      tasks: [
        { ...base, id: 'process.1', title: 'Run release command', uses: 'core/process', completedAt: '2026-01-01T00:01:00.000Z', output: { stdout: 'release-ready', exitCode: 0 } },
        { ...base, id: 'open-pr.1', title: 'Open release PR', uses: 'mohist/create-pull-request', completedAt: '2026-01-01T00:02:00.000Z', output: { kind: 'create-pull-request', prNumber: 42, prUrl: 'https://example.test/pr/42', mergeCommitSha: null, targetBranch: 'main' } },
        { ...base, id: 'no-output.1', title: 'Complete without output', uses: 'core/process', completedAt: '2026-01-01T00:03:00.000Z', output: null },
      ],
    }],
  }
}

describe('WorkflowView structured output', () => {
  beforeEach(() => { timeline = structuredTimeline() })

  it('renders process and PR fields without fabricating null output', async () => {
    const queryClient = createQueryClient()
    queryClient.setQueryData(['issues', 1, 'test-project', 'workflow-timeline'], timeline)
    const { container } = render(<WorkflowView issue={issue()} dependencies={dependencies} />, { queryClient: queryClient as QueryClient })

    for (const title of ['Run release command', 'Open release PR']) {
      fireEvent.click(screen.getByText(title).closest('button')!)
    }
    await act(async () => { await Promise.resolve() })

    expect(screen.getByText('Complete without output').closest('[data-testid="workflow-task-item"]')?.querySelector('button')).toBeNull()

    expect(screen.getByText(/"stdout": "release-ready"/)).toBeInTheDocument()
    expect(screen.getByText(/"exitCode": 0/)).toBeInTheDocument()
    expect(screen.getByText(/"prNumber": 42/)).toBeInTheDocument()
    expect(screen.getByText(/"mergeCommitSha": null/)).toBeInTheDocument()
    expect(container.querySelectorAll('pre')).toHaveLength(2)
  })
})
