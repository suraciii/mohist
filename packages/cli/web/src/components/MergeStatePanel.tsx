import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api } from '../lib/api'

interface MergeStatePanelProps {
  issueNumber: number
  mergeState: string | null | undefined
}

export function MergeStatePanel({ issueNumber, mergeState }: MergeStatePanelProps) {
  const queryClient = useQueryClient()

  const retryMutation = useMutation({
    mutationFn: () => api.retryMerge(issueNumber),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
    },
  })

  if (!mergeState) return null

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
          <span className="text-sm font-medium text-red-800">Build failed after merge</span>
        </div>
        <p className="mt-1 text-xs text-red-600">
          The merge was rolled back because the build failed. Review the changes and retry.
        </p>
        <button
          onClick={() => retryMutation.mutate()}
          disabled={retryMutation.isPending}
          className="mt-3 rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
        >
          {retryMutation.isPending ? 'Retrying...' : 'Retry Merge'}
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
          {retryMutation.isPending ? 'Retrying...' : 'Retry Merge'}
        </button>
        {retryMutation.error && (
          <div className="mt-2 text-xs text-amber-500">{retryMutation.error.message}</div>
        )}
      </div>
    )
  }

  return null
}
