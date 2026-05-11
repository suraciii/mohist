import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { onAgentEvent } from '../lib/agent-events'
import type { Issue, CheckSuite, CheckSuiteChecks, CheckState, CheckStateStatus } from '../lib/types'

const PENDING_STATE: CheckState = { status: 'pending' }

const DEFAULT_CHECKS: CheckSuiteChecks = {
  'review-passed': { ...PENDING_STATE },
  'merge-ready': { ...PENDING_STATE },
  'user-approval': { ...PENDING_STATE },
}

function resetChecksToPending(_checks: CheckSuiteChecks): CheckSuiteChecks {
  return {
    'review-passed': { ...PENDING_STATE },
    'merge-ready': { ...PENDING_STATE },
    'user-approval': { ...PENDING_STATE },
  }
}

export function useCheckSuiteProgress(issueNumber: number) {
  const queryClient = useQueryClient()
  const lastSnapshotShaRef = useRef<string | null>(null)

  useEffect(() => {
    const issueId = String(issueNumber)
    const unsubs: Array<() => void> = []

    unsubs.push(
      onAgentEvent('check_update', (event) => {
        if (event.issueId !== issueId) return

        const validStatuses: CheckStateStatus[] = ['pending', 'running', 'pass', 'fail']
        const checkStatus: CheckStateStatus = validStatuses.includes(event.status as CheckStateStatus)
          ? (event.status as CheckStateStatus)
          : 'pending'

        const isReset = event.snapshotSha && event.snapshotSha !== lastSnapshotShaRef.current
        if (event.snapshotSha) {
          lastSnapshotShaRef.current = event.snapshotSha
        }

        queryClient.setQueryData<Issue>(['issues', issueNumber], (old) => {
          if (!old) return old

          const currentSuite: CheckSuite = old.checkSuite ?? {
            id: '',
            issueId: old.id,
            snapshotSha: event.snapshotSha ?? '',
            status: 'running',
            checks: { ...DEFAULT_CHECKS },
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
          }

          const updatedChecks: CheckSuiteChecks = isReset
            ? resetChecksToPending(currentSuite.checks)
            : { ...currentSuite.checks }

          const checkName = event.checkName as keyof CheckSuiteChecks
          if (checkName in updatedChecks) {
            updatedChecks[checkName] = {
              status: checkStatus,
              ranAt: checkStatus === 'running' || checkStatus === 'pending'
                ? undefined
                : new Date().toISOString(),
            }
          }

          return {
            ...old,
            checkSuite: {
              ...currentSuite,
              snapshotSha: event.snapshotSha ?? currentSuite.snapshotSha,
              checks: updatedChecks,
              updatedAt: new Date().toISOString(),
            },
          }
        })
      }),
    )

    unsubs.push(
      onAgentEvent('check_suite_status_changed', (event) => {
        if (event.issueId !== issueId) return

        if (event.snapshotSha) {
          lastSnapshotShaRef.current = event.snapshotSha
        }

        queryClient.setQueryData<Issue>(['issues', issueNumber], (old) => {
          if (!old) return old

          const currentSuite: CheckSuite = old.checkSuite ?? {
            id: '',
            issueId: old.id,
            snapshotSha: event.snapshotSha ?? '',
            status: 'running',
            checks: { ...DEFAULT_CHECKS },
            createdAt: new Date().toISOString(),
            updatedAt: new Date().toISOString(),
          }

          return {
            ...old,
            checkSuite: {
              ...currentSuite,
              snapshotSha: event.snapshotSha ?? currentSuite.snapshotSha,
              status: event.suiteStatus as CheckSuite['status'],
              updatedAt: new Date().toISOString(),
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
