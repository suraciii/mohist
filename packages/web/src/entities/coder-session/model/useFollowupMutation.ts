import { useMutation, type UseMutationResult } from '@tanstack/react-query'
import { ApiError } from '@/shared/api/client'
import { useProject } from '../../project/@x/project-context'
import { postFollowup, type SessionFollowupResult } from '../api/client'

export interface FollowupMutationInput {
  issueNumber: number
  sessionName: string
  text: string
  attachments?: string[]
  idempotencyKey?: string
}

export function useFollowupMutation(): UseMutationResult<SessionFollowupResult, ApiError, FollowupMutationInput> {
  const { projectId } = useProject()
  return useMutation<SessionFollowupResult, ApiError, FollowupMutationInput>({
    mutationFn: ({ issueNumber, sessionName, text, idempotencyKey, attachments }) => {
      if (!projectId) {
        return Promise.reject(new ApiError('Project is required', 400))
      }
      return postFollowup(issueNumber, sessionName, text, projectId, idempotencyKey, attachments)
    },
  })
}
