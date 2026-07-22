import { Skeleton } from '@/shared/ui/components/skeleton'

export interface IssueDetailPageSkeletonProps {
  testId?: string
}

export function IssueDetailPageSkeleton({
  testId = 'issue-detail-page-skeleton',
}: IssueDetailPageSkeletonProps) {
  return (
    <div
      className="flex-1 min-w-0 overflow-y-auto"
      data-testid={testId}
      data-loading-state="initial"
    >
      <div className="max-w-4xl min-w-0 mx-auto px-4 sm:px-6 py-6">
        <div data-testid="status-header-tier-skeleton" className="space-y-4">
          <Skeleton className="h-12 w-2/3" data-testid={`${testId}-title`} />

          <div className="flex flex-wrap items-center gap-3">
            <Skeleton className="h-5 w-12" />
            <Skeleton className="h-5 w-16" />
            <Skeleton className="h-5 w-20" />
          </div>

          <Skeleton className="h-8 w-3/4" />

          <div className="flex gap-3">
            <Skeleton className="h-4 w-16" />
            <Skeleton className="h-4 w-16" />
            <Skeleton className="h-4 w-16" />
          </div>

          <Skeleton className="h-20 w-full" data-testid={`${testId}-decision-surface`} />
        </div>

        <div className="mt-8 grid min-w-0 grid-cols-1 lg:grid-cols-3 gap-8">
          <div className="min-w-0 lg:col-span-2 space-y-8" data-testid="reading-flow-skeleton">
            <Skeleton className="h-12 w-full" />
            <Skeleton className="h-64 w-full" />
            <Skeleton className="h-40 w-full" />
            <Skeleton className="h-32 w-full" />
          </div>

          <div
            className="min-w-0 space-y-4"
            data-testid="reference-rail-skeleton"
          >
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-24 w-full" />
            <Skeleton className="h-24 w-full" />
          </div>
        </div>
      </div>
    </div>
  )
}
