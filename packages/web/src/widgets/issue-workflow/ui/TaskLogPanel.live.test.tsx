import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { ProjectProvider } from '../../../entities/project'
import { LiveEventsContext, type LiveEventsApi } from '../../../shared/api/live-events'
import { TaskLogPanel, type TaskLogDataHook } from './TaskLogPanel'

const taskLogHook: TaskLogDataHook = () => ({
  data: { lines: [], nextCursor: null, truncated: false },
  isLoading: false,
  isError: false,
})

function renderPanel(
  api: LiveEventsApi,
  queryClient: QueryClient,
  props: { taskStatus?: 'running' | 'blocked' | 'pending' | 'failed'; workflowRunId?: string | null } = {},
) {
  return render(
    <QueryClientProvider client={queryClient}>
      <ProjectProvider initialProjectId="project-1">
        <LiveEventsContext.Provider value={api}>
          <TaskLogPanel
            issueNumber={42}
            taskId="task-1"
            workflowRunId={props.workflowRunId === undefined ? 'run-1' : props.workflowRunId}
            taskStatus={props.taskStatus ?? 'running'}
            taskLogHook={taskLogHook}
          />
        </LiveEventsContext.Provider>
      </ProjectProvider>
    </QueryClientProvider>,
  )
}

describe('TaskLogPanel live owner registration', () => {
  it('registers with the shared owner and disposes the admitted scope', () => {
    const dispose = vi.fn()
    const registerTaskLogScope = vi.fn(() => ({ admitted: true as const, dispose }))
    const api: LiveEventsApi = {
      registerTaskLogScope,
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }
    const rendered = renderPanel(api, new QueryClient())

    expect(registerTaskLogScope).toHaveBeenCalledWith(
      { workflowRunId: 'run-1', taskId: 'task-1' },
      expect.any(Function),
      expect.any(Function),
    )

    rendered.unmount()
    expect(dispose).toHaveBeenCalledOnce()
  })

  it('keeps blocked tasks registered for a late authoritative result', () => {
    const registerTaskLogScope = vi.fn(() => ({ admitted: true as const, dispose: vi.fn() }))
    const api: LiveEventsApi = {
      registerTaskLogScope,
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }

    renderPanel(api, new QueryClient(), { taskStatus: 'blocked' })

    expect(registerTaskLogScope).toHaveBeenCalledOnce()
  })

  it.each([
    ['pending status', { taskStatus: 'pending' as const }],
    ['missing workflow run', { workflowRunId: null }],
  ])('does not register for %s', (_name, props) => {
    const registerTaskLogScope = vi.fn(() => ({ admitted: true as const, dispose: vi.fn() }))
    const api: LiveEventsApi = {
      registerTaskLogScope,
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }

    renderPanel(api, new QueryClient(), props)

    expect(registerTaskLogScope).not.toHaveBeenCalled()
  })

  it('rejects live deltas outside the mounted project, workflow, or task scope', () => {
    let onDelta: Parameters<LiveEventsApi['registerTaskLogScope']>[1] | undefined
    const registerTaskLogScope = vi.fn((_: unknown, callback: typeof onDelta) => {
      onDelta = callback
      return { admitted: true as const, dispose: vi.fn() }
    })
    const api: LiveEventsApi = {
      registerTaskLogScope,
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }
    const queryClient = new QueryClient()
    const setQueryData = vi.spyOn(queryClient, 'setQueryData')
    renderPanel(api, queryClient)
    const base = {
      ownerKind: 'workflow',
      ownerId: 'run-1',
      projectId: 'project-1',
      workId: 'work-1',
      taskId: 'task-1',
      entries: [{ seq: 1, timestamp: '2026-08-20T12:00:00Z', source: 'runner', text: 'line' }],
      truncated: false,
    }

    act(() => {
      onDelta?.({ ...base, projectId: 'project-2' })
      onDelta?.({ ...base, ownerId: 'run-2' })
      onDelta?.({ ...base, taskId: 'task-2' })
    })

    expect(setQueryData).not.toHaveBeenCalled()
  })

  it('uses the mounted registration refetch during reconnect reconciliation', async () => {
    let refetch: Parameters<LiveEventsApi['registerTaskLogScope']>[2] | undefined
    const api: LiveEventsApi = {
      registerTaskLogScope: (_scope, _onDelta, callback) => {
        refetch = callback
        return { admitted: true, dispose: vi.fn() }
      },
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }
    const queryClient = new QueryClient()
    const refetchQueries = vi.spyOn(queryClient, 'refetchQueries').mockResolvedValue(undefined)
    renderPanel(api, queryClient)

    await refetch?.(new AbortController().signal)

    expect(refetchQueries).toHaveBeenCalledWith(
      {
        queryKey: expect.arrayContaining(['task-log']),
        exact: true,
      },
      { throwOnError: true },
    )
  })

  it('disposes the live registration and invalidates the authoritative query on a terminal transition', () => {
    const dispose = vi.fn()
    const api: LiveEventsApi = {
      registerTaskLogScope: () => ({ admitted: true, dispose }),
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }
    const queryClient = new QueryClient()
    const invalidateQueries = vi.spyOn(queryClient, 'invalidateQueries')
    const rendered = renderPanel(api, queryClient)

    rendered.rerender(
      <QueryClientProvider client={queryClient}>
        <ProjectProvider initialProjectId="project-1">
          <LiveEventsContext.Provider value={api}>
            <TaskLogPanel
              issueNumber={42}
              taskId="task-1"
              workflowRunId="run-1"
              taskStatus="failed"
              taskLogHook={taskLogHook}
            />
          </LiveEventsContext.Provider>
        </ProjectProvider>
      </QueryClientProvider>,
    )

    expect(dispose).toHaveBeenCalledOnce()
    expect(invalidateQueries).toHaveBeenCalledWith({
      queryKey: expect.arrayContaining(['task-log']),
      exact: true,
    })
  })

  it('polls the authoritative HTTP query when the owner rejects a 129th scope', async () => {
    vi.useFakeTimers()
    const queryClient = new QueryClient()
    const refetch = vi.spyOn(queryClient, 'refetchQueries').mockRejectedValue(new Error('unavailable'))
    const api: LiveEventsApi = {
      registerTaskLogScope: () => ({ admitted: false }),
      registerTranscriptReconciliation: () => ({ dispose: () => {} }),
    }
    const rendered = renderPanel(api, queryClient)

    act(() => {
      vi.advanceTimersByTime(2000)
    })
    await Promise.resolve()

    expect(refetch).toHaveBeenCalledWith(
      {
        queryKey: expect.arrayContaining(['task-log']),
        exact: true,
      },
      { throwOnError: true },
    )
    rendered.unmount()
  })
})
