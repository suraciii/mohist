import { useMutation, useQueryClient, type UseMutationResult } from '@tanstack/react-query'
import {
  addComment,
  addPrerequisite,
  approveIssue,
  closeIssue,
  deleteComment,
  extractAttachmentIds,
  forceStopIssue,
  invalidateApprovalWait,
  removePrerequisite,
  reopenIssue,
  rejectIssue,
  rerunIssue,
  resumeIssue,
  retryIssue,
  startIssue,
  stopIssue,
  updateIssue,
} from '../../../entities/issue'

export interface UseIssueDetailMutationsOptions {
  issueNumber: number
  projectId: string | null | undefined
  onForceStopSuccess?: () => void
  onStopSuccess?: () => void
  onAddCommentSuccess?: () => void
  onDeleteCommentSuccess?: () => void
  onDeleteCommentError?: (error: Error) => void
}

export interface IssueDetailMutations {
  startMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  approveMutation: UseMutationResult<{ issue: unknown; context?: unknown; message: string }, Error, void, unknown>
  sendBackMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  markReadyMutation: UseMutationResult<unknown, Error, void, unknown>
  addPrerequisiteMutation: UseMutationResult<{ issue: unknown; message: string }, Error, number, unknown>
  removePrerequisiteMutation: UseMutationResult<{ issue: unknown; message: string }, Error, number, unknown>
  closeMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  forceStopMutation: UseMutationResult<{ ok: boolean; issueNumber: number }, Error, void, unknown>
  stopMutation: UseMutationResult<{ ok: boolean; issueNumber: number }, Error, void, unknown>
  reopenMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  resumeMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  retryMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  rerunMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  addCommentMutation: UseMutationResult<unknown, Error, string, unknown>
  deleteCommentMutation: UseMutationResult<{ message: string }, Error, string, unknown>
}

export function useIssueDetailMutations({
  issueNumber,
  projectId,
  onForceStopSuccess,
  onStopSuccess,
  onAddCommentSuccess,
  onDeleteCommentSuccess,
  onDeleteCommentError,
}: UseIssueDetailMutationsOptions): IssueDetailMutations {
  const queryClient = useQueryClient()

  const invalidateRuntimeQueries = () => {
    queryClient.invalidateQueries({ queryKey: ['issues'] })
    queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
  }

  const invalidateApprovalQueries = () => {
    invalidateRuntimeQueries()
    invalidateApprovalWait(queryClient)
  }

  const startMutation = useMutation({
    mutationFn: () => startIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
    onError: (err: Error) => {
      if (err.message.includes('waiting for')) {
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      }
    },
  })

  const approveMutation = useMutation({
    mutationFn: () => approveIssue(issueNumber, projectId),
    onSuccess: invalidateApprovalQueries,
  })

  const sendBackMutation = useMutation({
    mutationFn: () => rejectIssue(issueNumber, {}, projectId),
    onSuccess: invalidateApprovalQueries,
  })

  const markReadyMutation = useMutation({
    mutationFn: () => updateIssue(issueNumber, { isDraft: false }, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const addPrerequisiteMutation = useMutation({
    mutationFn: (prerequisiteNumber: number) => addPrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const removePrerequisiteMutation = useMutation({
    mutationFn: (prerequisiteNumber: number) => removePrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  })

  const closeMutation = useMutation({
    mutationFn: () => closeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const forceStopMutation = useMutation({
    mutationFn: () => forceStopIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onForceStopSuccess?.()
    },
  })

  const stopMutation = useMutation({
    mutationFn: () => stopIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onStopSuccess?.()
    },
  })

  const reopenMutation = useMutation({
    mutationFn: () => reopenIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const resumeMutation = useMutation({
    mutationFn: () => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const retryMutation = useMutation({
    mutationFn: () => retryIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const rerunMutation = useMutation({
    mutationFn: () => rerunIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  const addCommentMutation = useMutation({
    mutationFn: (body: string) => addComment(issueNumber, body, projectId, extractAttachmentIds(body)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      onAddCommentSuccess?.()
    },
  })

  const deleteCommentMutation = useMutation({
    mutationFn: (commentId: string) => deleteComment(issueNumber, commentId, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      onDeleteCommentSuccess?.()
    },
    onError: (err) => {
      onDeleteCommentError?.(err instanceof Error ? err : new Error('Failed to delete comment'))
    },
  })

  return {
    startMutation,
    approveMutation,
    sendBackMutation,
    markReadyMutation,
    addPrerequisiteMutation,
    removePrerequisiteMutation,
    closeMutation,
    forceStopMutation,
    stopMutation,
    reopenMutation,
    resumeMutation,
    retryMutation,
    rerunMutation,
    addCommentMutation,
    deleteCommentMutation,
  }
}
