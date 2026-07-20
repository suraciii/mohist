import { useMutation, useQueryClient, type QueryClient, type UseMutationResult } from '@tanstack/react-query'
import {
  addComment,
  addPrerequisite,
  approveIssue,
  closeIssue,
  deleteComment,
  extractAttachmentIds,
  forceStopIssue,
  invalidateApprovalWait,
  markIssueDone,
  removePrerequisite,
  reopenIssue,
  requestChangesIssue,
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
  sendBackMutation: UseMutationResult<unknown, Error, { stage: string; body: string }, unknown>
  markReadyMutation: UseMutationResult<unknown, Error, void, unknown>
  addPrerequisiteMutation: UseMutationResult<{ issue: unknown; message: string }, Error, number, unknown>
  removePrerequisiteMutation: UseMutationResult<{ issue: unknown; message: string }, Error, number, unknown>
  closeMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  markDoneMutation: UseMutationResult<void, Error, void, unknown>
  forceStopMutation: UseMutationResult<{ ok: boolean; issueNumber: number }, Error, void, unknown>
  stopMutation: UseMutationResult<{ ok: boolean; issueNumber: number }, Error, void, unknown>
  reopenMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  resumeMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  retryMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  rerunMutation: UseMutationResult<{ issue: unknown; message: string }, Error, void, unknown>
  addCommentMutation: UseMutationResult<unknown, Error, string, unknown>
  deleteCommentMutation: UseMutationResult<{ message: string }, Error, string, unknown>
}

export interface IssueDetailMutationDependencies {
  addComment: typeof addComment
  addPrerequisite: typeof addPrerequisite
  approveIssue: typeof approveIssue
  closeIssue: typeof closeIssue
  deleteComment: typeof deleteComment
  extractAttachmentIds: typeof extractAttachmentIds
  forceStopIssue: typeof forceStopIssue
  invalidateApprovalWait: typeof invalidateApprovalWait
  markIssueDone: typeof markIssueDone
  removePrerequisite: typeof removePrerequisite
  reopenIssue: typeof reopenIssue
  requestChangesIssue: typeof requestChangesIssue
  rerunIssue: typeof rerunIssue
  resumeIssue: typeof resumeIssue
  retryIssue: typeof retryIssue
  startIssue: typeof startIssue
  stopIssue: typeof stopIssue
  updateIssue: typeof updateIssue
}

const defaultDependencies: IssueDetailMutationDependencies = {
  addComment,
  addPrerequisite,
  approveIssue,
  closeIssue,
  deleteComment,
  extractAttachmentIds,
  forceStopIssue,
  invalidateApprovalWait,
  markIssueDone,
  removePrerequisite,
  reopenIssue,
  requestChangesIssue,
  rerunIssue,
  resumeIssue,
  retryIssue,
  startIssue,
  stopIssue,
  updateIssue,
}

export function createIssueDetailMutationOptions(
  {
    issueNumber,
    projectId,
    onForceStopSuccess,
    onStopSuccess,
    onAddCommentSuccess,
    onDeleteCommentSuccess,
    onDeleteCommentError,
  }: UseIssueDetailMutationsOptions,
  queryClient: QueryClient,
  dependencies: IssueDetailMutationDependencies = defaultDependencies,
) {
  const {
    addComment,
    addPrerequisite,
    approveIssue,
    closeIssue,
    deleteComment,
    extractAttachmentIds,
    forceStopIssue,
    invalidateApprovalWait,
    markIssueDone,
    removePrerequisite,
    reopenIssue,
    requestChangesIssue,
    rerunIssue,
    resumeIssue,
    retryIssue,
    startIssue,
    stopIssue,
    updateIssue,
  } = dependencies

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

  const startMutation = {
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
  }

  const approveMutation = {
    mutationFn: () => approveIssue(issueNumber, projectId),
    onSuccess: invalidateApprovalQueries,
  }

  const sendBackMutation = {
    mutationFn: ({ stage, body }: { stage: string; body: string }) => requestChangesIssue(issueNumber, { stage, body }, projectId),
    onSuccess: invalidateApprovalQueries,
  }

  const markReadyMutation = {
    mutationFn: () => updateIssue(issueNumber, { isDraft: false }, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  }

  const addPrerequisiteMutation = {
    mutationFn: (prerequisiteNumber: number) => addPrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  }

  const removePrerequisiteMutation = {
    mutationFn: (prerequisiteNumber: number) => removePrerequisite(issueNumber, prerequisiteNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  }

  const closeMutation = {
    mutationFn: () => closeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  }

  const markDoneMutation = {
    mutationFn: () => markIssueDone(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
    },
  }

  const forceStopMutation = {
    mutationFn: () => forceStopIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onForceStopSuccess?.()
    },
  }

  const stopMutation = {
    mutationFn: () => stopIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      onStopSuccess?.()
    },
  }

  const reopenMutation = {
    mutationFn: () => reopenIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  }

  const resumeMutation = {
    mutationFn: () => resumeIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  }

  const retryMutation = {
    mutationFn: () => retryIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  }

  const rerunMutation = {
    mutationFn: () => rerunIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  }

  const addCommentMutation = {
    mutationFn: (body: string) => addComment(issueNumber, body, projectId, extractAttachmentIds(body)),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      onAddCommentSuccess?.()
    },
  }

  const deleteCommentMutation = {
    mutationFn: (commentId: string) => deleteComment(issueNumber, commentId, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
      onDeleteCommentSuccess?.()
    },
    onError: (err: unknown) => {
      onDeleteCommentError?.(err instanceof Error ? err : new Error('Failed to delete comment'))
    },
  }

  return {
    startMutation,
    approveMutation,
    sendBackMutation,
    markReadyMutation,
    addPrerequisiteMutation,
    removePrerequisiteMutation,
    closeMutation,
    markDoneMutation,
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

export function useIssueDetailMutations(
  options: UseIssueDetailMutationsOptions,
  dependencyOverrides: Partial<IssueDetailMutationDependencies> = {},
): IssueDetailMutations {
  const queryClient = useQueryClient()
  const mutationOptions = createIssueDetailMutationOptions(
    options,
    queryClient,
    { ...defaultDependencies, ...dependencyOverrides },
  )

  return {
    startMutation: useMutation(mutationOptions.startMutation),
    approveMutation: useMutation(mutationOptions.approveMutation),
    sendBackMutation: useMutation(mutationOptions.sendBackMutation),
    markReadyMutation: useMutation(mutationOptions.markReadyMutation),
    addPrerequisiteMutation: useMutation(mutationOptions.addPrerequisiteMutation),
    removePrerequisiteMutation: useMutation(mutationOptions.removePrerequisiteMutation),
    closeMutation: useMutation(mutationOptions.closeMutation),
    markDoneMutation: useMutation(mutationOptions.markDoneMutation),
    forceStopMutation: useMutation(mutationOptions.forceStopMutation),
    stopMutation: useMutation(mutationOptions.stopMutation),
    reopenMutation: useMutation(mutationOptions.reopenMutation),
    resumeMutation: useMutation(mutationOptions.resumeMutation),
    retryMutation: useMutation(mutationOptions.retryMutation),
    rerunMutation: useMutation(mutationOptions.rerunMutation),
    addCommentMutation: useMutation(mutationOptions.addCommentMutation),
    deleteCommentMutation: useMutation<{ message: string }, Error, string>(mutationOptions.deleteCommentMutation),
  }
}
