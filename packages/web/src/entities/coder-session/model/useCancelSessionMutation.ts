import { useMutation, type UseMutationResult } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { useProject } from '../../project/@x/project-context'
import { cancelSession, type SessionCancelResult } from '../api/client'

export interface CancelSessionMutationInput {
  issueNumber: number
  sessionName: string
}

export function useCancelSessionMutation(): UseMutationResult<SessionCancelResult, ApiError, CancelSessionMutationInput> {
  const { projectId } = useProject()
  return useMutation<SessionCancelResult, ApiError, CancelSessionMutationInput>({
    mutationFn: ({ issueNumber, sessionName }) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return cancelSession(issueNumber, sessionName, projectId)
    },
  })
}
