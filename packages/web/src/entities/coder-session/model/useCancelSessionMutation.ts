import { useMutation, useQueryClient, type QueryClient, type UseMutationResult } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { useProject } from '../../project/@x/project-context'
import { cancelSession, type SessionCancelResult } from '../api/client'

export interface CancelSessionMutationInput {
  issueNumber: number
  sessionName: string
}

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function cancelSessionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ issueNumber, sessionName }: CancelSessionMutationInput) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return cancelSession(issueNumber, sessionName, projectId)
    },
    onSuccess: (_result: SessionCancelResult, { issueNumber, sessionName }: CancelSessionMutationInput) => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, projectId, 'coder-sessions'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, projectId, 'agent-session-metadata', sessionName] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber, projectId, 'agent-session-transcript', sessionName] })
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
