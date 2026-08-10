import { useMutation, useQueryClient, type QueryClient, type UseMutationResult } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { useProject } from '../../project/@x/project-context'
import { issueWorkflowKeys } from '../../issue/@x/query-keys'
import { stopSession, type SessionStopResult } from '../api/client'

export interface StopSessionMutationInput {
  issueNumber: number
  sessionName: string
  turnId: string
  idempotencyKey?: string
}

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function stopSessionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (input: StopSessionMutationInput) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return stopSession(input.issueNumber, input.sessionName, input.turnId, projectId, input.idempotencyKey)
    },
    onSuccess: (_result: SessionStopResult, { issueNumber, sessionName }: StopSessionMutationInput) => {
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'coder-sessions'), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'session-metadata', sessionName), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'session-transcript', sessionName) })
      queryClient.invalidateQueries({ queryKey: ['workflow-runs'] })
    },
  }
}

export function useStopSessionMutation(): UseMutationResult<SessionStopResult, ApiError, StopSessionMutationInput> {
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  return useMutation<SessionStopResult, ApiError, StopSessionMutationInput>(
    stopSessionMutationOptions(projectId, queryClient),
  )
}
