import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'

export type ReviewDimension = {
  name: string
  status: string
  issues?: string[]
}

export type ReviewOutput = {
  result?: string
  dimensions?: ReviewDimension[]
  reviewReport?: string
  selfReviewNotes?: string
}

export function parseReviewOutput(output?: Record<string, unknown>): ReviewOutput {
  if (!output) return {}
  const result =
    typeof output.result === 'string'
      ? output.result
      : typeof output.verdict === 'string'
        ? output.verdict
        : undefined
  const dimensions = Array.isArray(output.dimensions)
    ? output.dimensions
        .filter((d): d is Record<string, unknown> => typeof d === 'object' && d !== null)
        .map((d) => ({
          name: typeof d.name === 'string' ? d.name : 'Unknown',
          status: typeof d.status === 'string' ? d.status : 'Unknown',
          issues: Array.isArray(d.issues)
            ? d.issues.filter((i): i is string => typeof i === 'string')
            : undefined,
        }))
    : undefined
  const reviewReport =
    typeof output.reviewReport === 'string' ? output.reviewReport : undefined
  const selfReviewNotes =
    typeof output.selfReviewNotes === 'string' ? output.selfReviewNotes : undefined
  return { result, dimensions, reviewReport, selfReviewNotes }
}

function classifyResult(result?: string): 'PASS' | 'FAIL' | 'UNKNOWN' {
  if (!result) return 'UNKNOWN'
  const upper = result.toUpperCase()
  if (upper === 'PASS') return 'PASS'
  if (upper === 'FAIL') return 'FAIL'
  return 'UNKNOWN'
}

function CheckIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function XIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function ResultBanner({ classified, review }: { classified: 'PASS' | 'FAIL' | 'UNKNOWN'; review: ReviewOutput }) {
  if (classified === 'PASS') {
    const treatment: StatusTreatment = statusTreatment('workflow-run', 'completed')
    const dims = review.dimensions ?? []
    const passCount = dims.filter((d) => d.status.toUpperCase() === 'PASS').length
    const total = dims.length
    const ratio = total > 0 ? `${passCount}/${total}` : ''
    return (
      <div
        data-testid="review-result-banner"
        data-family={treatment.family}
        className={`rounded-lg border p-4 flex items-start gap-3 ${treatment.container} ${treatment.border}`}
      >
        <CheckIcon className={`h-6 w-6 shrink-0 mt-0.5 ${treatment.text}`} />
        <div>
          <div className={`text-sm font-semibold ${treatment.text}`}>All checks passed</div>
          {ratio && (
            <div className={`text-xs mt-0.5 ${treatment.text}/80`}>{ratio} dimensions passed</div>
          )}
        </div>
      </div>
    )
  }

  if (classified === 'FAIL') {
    const treatment: StatusTreatment = statusTreatment('workflow-run', 'failed')
    const failDims = (review.dimensions ?? []).filter(
      (d) => d.status.toUpperCase() === 'FAIL',
    )
    const totalIssues = failDims.reduce((sum, d) => sum + (d.issues?.length ?? 0), 0)
    const failNames = failDims.map((d) => d.name)
    return (
      <div
        data-testid="review-result-banner"
        data-family={treatment.family}
        className={`rounded-lg border p-4 flex items-start gap-3 ${treatment.container} ${treatment.border}`}
      >
        <XIcon className={`h-6 w-6 shrink-0 mt-0.5 ${treatment.text}`} />
        <div>
          <div className={`text-sm font-semibold ${treatment.text}`}>
            {totalIssues > 0 ? `${totalIssues} issue${totalIssues !== 1 ? 's' : ''} found` : 'Issues found'}
          </div>
          {failNames.length > 0 && (
            <div className={`text-xs mt-0.5 ${treatment.text}/80`}>{failNames.join(' · ')}</div>
          )}
        </div>
      </div>
    )
  }

  const treatment: StatusTreatment = statusTreatment('workflow-run', 'pending')
  return (
    <div
      data-testid="review-result-banner"
      data-family={treatment.family}
      className={`rounded-lg border p-4 flex items-start gap-3 ${treatment.container} ${treatment.border}`}
    >
      <svg className={`h-6 w-6 shrink-0 mt-0.5 ${treatment.text}`} viewBox="0 0 20 20" fill="currentColor">
        <path
          fillRule="evenodd"
          d="M18 10a8 8 0 11-16 0 8 8 0 0116 0zm-8-5a.75.75 0 01.75.75v4.5a.75.75 0 01-1.5 0v-4.5A.75.75 0 0110 5zm0 10a1 1 0 100-2 1 1 0 000 2z"
          clipRule="evenodd"
        />
      </svg>
      <div className={`text-sm font-semibold ${treatment.text}`}>Review required</div>
    </div>
  )
}

function IssueSummary({ review, classified }: { review: ReviewOutput; classified: 'PASS' | 'FAIL' | 'UNKNOWN' }) {
  const dims = review.dimensions ?? []
  const failDims = dims.filter((d) => d.status.toUpperCase() === 'FAIL')
  const passDims = dims.filter((d) => d.status.toUpperCase() === 'PASS')
  const failedTreatment = statusTreatment('workflow-run', 'failed')
  const successTreatment = statusTreatment('workflow-run', 'completed')

  if (dims.length === 0) {
    if (classified === 'FAIL') {
      return (
        <div className="text-sm text-muted-foreground mt-3">
          Issues found. View full report for details.
        </div>
      )
    }
    return null
  }

  return (
    <div className="mt-3 space-y-2">
      {failDims.length > 0 && (
        <div className="space-y-2">
          {failDims.map((dim) => (
            <div
              key={dim.name}
              className={`rounded-md border p-3 ${failedTreatment.container} ${failedTreatment.border}`}
            >
              <div className="flex items-center gap-1.5 mb-1">
                <span className={`inline-block h-2 w-2 rounded-full shrink-0 ${failedTreatment.dot}`} />
                <span className="text-sm font-medium text-foreground">{dim.name}</span>
              </div>
              {dim.issues && dim.issues.length > 0 ? (
                <ul className="ml-3.5 space-y-0.5">
                  {dim.issues.map((issue, i) => (
                    <li key={i} className="text-sm text-muted-foreground list-disc">
                      {issue}
                    </li>
                  ))}
                </ul>
              ) : (
                <div className="text-sm text-muted-foreground ml-3.5">No specific issues listed</div>
              )}
            </div>
          ))}
        </div>
      )}

      {passDims.length > 0 && (
        <div className="flex items-center gap-1.5 py-1">
          <svg className={`h-4 w-4 shrink-0 ${successTreatment.text}`} viewBox="0 0 20 20" fill="currentColor">
            <path
              fillRule="evenodd"
              d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z"
              clipRule="evenodd"
            />
          </svg>
          <span className="text-sm text-muted-foreground">
            {passDims.map((d) => d.name).join(' · ')}
          </span>
        </div>
      )}
    </div>
  )
}

interface ReviewSummaryProps {
  output?: Record<string, unknown>
}

export function ReviewSummary({ output }: ReviewSummaryProps) {
  const review = parseReviewOutput(output)
  const classified = classifyResult(review.result)

  return (
    <div>
      <ResultBanner classified={classified} review={review} />
      <IssueSummary review={review} classified={classified} />
    </div>
  )
}