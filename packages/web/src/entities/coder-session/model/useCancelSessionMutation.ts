import { useMutation, useQueryClient, type QueryClient, type UseMutationResult } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { useProject } from '../../project/@x/project-context'
import { issueWorkflowKeys } from '../../issue/@x/query-keys'
import { cancelSession, stopSession, type SessionCancelResult } from '../api/client'

export interface CancelSessionMutationInput {
  issueNumber: number
  sessionName: string
  turnId: string
  operation: 'cancel' | 'stop'
}

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function cancelSessionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (input: CancelSessionMutationInput) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return input.operation === 'stop'
        ? stopSession(input.issueNumber, input.sessionName, input.turnId, projectId)
        : cancelSession(input.issueNumber, input.sessionName, input.turnId, projectId)
    },
    onSuccess: (_result: SessionCancelResult, { issueNumber, sessionName }: CancelSessionMutationInput) => {
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'coder-sessions'), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'session-metadata', sessionName), exact: true })
      queryClient.invalidateQueries({ queryKey: issueWorkflowKeys.session(projectId, issueNumber, 'session-transcript', sessionName) })
      queryClient.invalidateQueries({ queryKey: ['workflow-runs'] })
    },
  }
}

export function useCancelSessionMutation(): UseMutationResult<SessionCancelResult, ApiError, CancelSessionMutationInput> {
  const { projectId } = useProject()
  const queryClient = useQueryClient()
  return useMutation<SessionCancelResult, ApiError, CancelSessionMutationInput>(
    cancelSessionMutationOptions(projectId, queryClient),
  )
}
