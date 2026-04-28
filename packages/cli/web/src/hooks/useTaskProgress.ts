import { useEffect } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../lib/agent-events'
import type { BuildStatus, Task } from '../lib/types'

export function useTaskProgress(issueNumber: number) {
  const queryClient = useQueryClient()

  useEffect(() => {
    const issueId = String(issueNumber)
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('ralph_task_update', (event) => {
        if (event.issueId !== issueId) return

        queryClient.setQueryData<{ version: number; tasks: Task[] }>(['issues', issueNumber, 'tasks'], (old) => {
          if (!old) return old
          return {
            ...old,
            tasks: old.tasks.map((task) => {
              if (task.id !== event.taskId) return task
              return {
                ...task,
                passes: event.status === 'completed',
                error: event.status === 'failed' ? event.error ?? null : task.error,
                attempts: event.attempt ?? task.attempts,
              }
            }),
          }
        })

        queryClient.setQueryData<BuildStatus>(['issues', issueNumber, 'build-status'], (old) => {
          if (!old) return old
          const tasks = old.tasks.map((task) => {
            if (task.id !== event.taskId) return task
            return {
              ...task,
              passes: event.status === 'completed',
              error: event.status === 'failed' ? event.error ?? null : task.error,
              attempts: event.attempt ?? task.attempts,
            }
          })
          return {
            ...old,
            tasks,
            progress: {
              ...old.progress,
              currentTask: event.status === 'started' ? event.taskId : old.progress.currentTask,
            },
          }
        })
      }),
    )

    unsubs.push(
      onAgentEvent('ralph_loop_progress', (event) => {
        if (event.issueId !== issueId) return

        queryClient.setQueryData<BuildStatus>(['issues', issueNumber, 'build-status'], (old) => {
          if (!old) return old
          return {
            ...old,
            progress: {
              ...old.progress,
              completed: event.completed,
              failed: event.failed,
              total: event.total,
            },
          }
        })
      }),
    )

    return () => {
      for (const unsub of unsubs) unsub()
    }
  }, [issueNumber, queryClient])
}
