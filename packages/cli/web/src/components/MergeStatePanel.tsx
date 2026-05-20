import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'
import { IssueStatus, Stage, type Issue, type WorkflowDeliveryRequirement } from '../lib/types'
import { isCompletedWithoutLocalMergeRequirement, isFalseDoneIssue } from '../lib/delivery-requirement'

interface MergeStatePanelProps {
  issueNumber: number
  mergeState: Issue['mergeState']
  stage: Stage
  status: IssueStatus
  deliveryRequirement?: WorkflowDeliveryRequirement
}

export function MergeStatePanel({ issueNumber, mergeState, stage, status, deliveryRequirement }: MergeStatePanelProps) {
  const queryClient = useQueryClient()

  const retryMutation = useMutation({
    mutationFn: () => api.retryMerge(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  const issueLifecycle = { stage, status, mergeState, deliveryRequirement }

  if (isFalseDoneIssue(issueLifecycle)) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-red-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-red-800">Done but not merged</span>
        </div>
        <p className="mt-1 text-xs text-red-600">
          This workflow requires local merge evidence, but no merged state was recorded.
        </p>
      </div>
    )
  }

  if (isCompletedWithoutLocalMergeRequirement(issueLifecycle)) {
    return (
      <div className="rounded-lg border border-green-200 bg-green-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-green-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M16.704 5.29a1 1 0 010 1.42l-7.25 7.25a1 1 0 01-1.42 0L3.296 9.22a1 1 0 111.414-1.414l4.034 4.034 6.543-6.543a1 1 0 011.417-.006z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-green-800">Workflow completed</span>
        </div>
        <p className="mt-1 text-xs text-green-700">
          This workflow is complete. No local merge evidence is required.
        </p>
      </div>
    )
  }

  if (mergeState === null || mergeState === undefined) {
    if (stage === Stage.Check) {
      return (
        <div className="rounded-lg border border-gray-200 bg-gray-50 p-4">
          <div className="flex items-center gap-2">
            <svg className="h-4 w-4 text-gray-500" viewBox="0 0 20 20" fill="currentColor">
              <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm.75-13a.75.75 0 00-1.5 0v5c0 .414.336.75.75.75h3a.75.75 0 000-1.5h-2.25V5z" clipRule="evenodd" />
            </svg>
            <span className="text-sm font-medium text-gray-700">Awaiting stage approval</span>
          </div>
          <p className="mt-1 text-xs text-gray-500">
            This issue is in check stage. Approve to continue the workflow.
          </p>
        </div>
      )
    }

    return (
      <div className="rounded-lg border border-gray-200 bg-gray-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm0-2a6 6 0 100-12 6 6 0 000 12z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-gray-600">Not ready for merge</span>
        </div>
        <p className="mt-1 text-xs text-gray-400">
          This issue has not reached the merge stage yet.
        </p>
      </div>
    )
  }

  if (mergeState === 'pending') {
    return (
      <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
        <div className="flex items-center gap-2">
          <span className="inline-block h-2 w-2 rounded-full bg-blue-400" />
          <span className="text-sm font-medium text-blue-800">Queued for merge</span>
        </div>
        <p className="mt-1 text-xs text-blue-600">
          This issue is waiting in the merge queue and will be merged automatically.
        </p>
      </div>
    )
  }

  if (mergeState === 'merging') {
    return (
      <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 animate-spin text-blue-600" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          <span className="text-sm font-medium text-blue-800">Merging...</span>
        </div>
        <p className="mt-1 text-xs text-blue-600">
          Merging changes and verifying build.
        </p>
      </div>
    )
  }

  if (mergeState === 'merged') {
    return (
      <div className="rounded-lg border border-green-200 bg-green-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-green-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-green-800">Merged</span>
        </div>
        <p className="mt-1 text-xs text-green-600">
          Changes merged and build verified successfully.
        </p>
      </div>
    )
  }

  if (mergeState === 'build-failed') {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-red-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-red-800">Build verification failed</span>
        </div>
        <p className="mt-1 text-xs text-red-600">
          Build check failed before merge. The merge was not performed. Review the changes and retry.
        </p>
        <button
          onClick={() => retryMutation.mutate()}
          disabled={retryMutation.isPending}
          className="mt-3 rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {retryMutation.isPending ? 'Rebasing and retrying...' : 'Rebase and Retry'}
        </button>
        {retryMutation.error && (
          <div className="mt-2 text-xs text-red-500">{retryMutation.error.message}</div>
        )}
      </div>
    )
  }

  if (mergeState === 'conflict') {
    return (
      <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-amber-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.168 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 6a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 6zm0 9a1 1 0 100-2 1 1 0 000 2z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-amber-800">Merge conflict</span>
        </div>
        <p className="mt-1 text-xs text-amber-600">
          The merge could not be completed due to conflicting changes. Resolve the conflict and retry.
        </p>
        <button
          onClick={() => retryMutation.mutate()}
          disabled={retryMutation.isPending}
          className="mt-3 rounded-md bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700 disabled:opacity-50 transition-colors"
        >
          {retryMutation.isPending ? 'Rebasing and retrying...' : 'Rebase and Retry'}
        </button>
        {retryMutation.error && (
          <div className="mt-2 text-xs text-amber-500">{retryMutation.error.message}</div>
        )}
      </div>
    )
  }

  if (mergeState === 'rebasing') {
    return (
      <div className="rounded-lg border border-blue-200 bg-blue-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 animate-spin text-blue-600" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          <span className="text-sm font-medium text-blue-800">Rebasing...</span>
        </div>
        <p className="mt-1 text-xs text-blue-600">
          Rebasing branch onto latest master.
        </p>
      </div>
    )
  }

  if (mergeState === 'resolving') {
    return (
      <div className="rounded-lg border border-amber-200 bg-amber-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 animate-spin text-amber-600" viewBox="0 0 24 24" fill="none">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
          </svg>
          <span className="text-sm font-medium text-amber-800">Resolving conflicts...</span>
        </div>
        <p className="mt-1 text-xs text-amber-600">
          Agent is resolving merge conflicts.
        </p>
      </div>
    )
  }

  if (mergeState === 'blocked') {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4">
        <div className="flex items-center gap-2">
          <svg className="h-4 w-4 text-red-600" viewBox="0 0 20 20" fill="currentColor">
            <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
          </svg>
          <span className="text-sm font-medium text-red-800">Merge blocked</span>
        </div>
        <p className="mt-1 text-xs text-red-600">
          Merge could not be completed. Review and retry manually.
        </p>
        <button
          onClick={() => retryMutation.mutate()}
          disabled={retryMutation.isPending}
          className="mt-3 rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {retryMutation.isPending ? 'Rebasing and retrying...' : 'Rebase and Retry'}
        </button>
        {retryMutation.error && (
          <div className="mt-2 text-xs text-red-500">{retryMutation.error.message}</div>
        )}
      </div>
    )
  }

  return null
}
