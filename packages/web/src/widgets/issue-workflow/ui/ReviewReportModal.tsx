import { useEffect } from 'react'
import Markdown from 'react-markdown'
import { Button } from '@/shared/ui/components/button'
import { statusTreatment, type StatusTreatment } from '@/shared/status-presentation'
import type { ReviewOutput } from './ReviewSummary'

const REVIEW_STATE_BY_CLASSIFIED = {
  PASS: { kind: 'workflow-run' as const, state: 'completed' },
  FAIL: { kind: 'workflow-run' as const, state: 'failed' },
  UNKNOWN: { kind: 'workflow-run' as const, state: 'pending' },
}

export function ResultBadge({ classified }: { classified: 'PASS' | 'FAIL' | 'UNKNOWN' }) {
  const binding = REVIEW_STATE_BY_CLASSIFIED[classified]
  const treatment: StatusTreatment = statusTreatment(binding.kind, binding.state)
  const Icon = classified === 'PASS'
    ? () => (
        <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
      )
    : () => (
        <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
      )
  const label = classified === 'UNKNOWN' ? 'REVIEW' : classified
  return (
    <span
      data-testid={`review-result-${classified.toLowerCase()}`}
      data-family={treatment.family}
      className={`inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-semibold ${treatment.container}`}
    >
      <Icon />
      {label}
    </span>
  )
}

export function FullReportModal({
  review,
  classified,
  onClose,
}: {
  review: ReviewOutput
  classified: 'PASS' | 'FAIL' | 'UNKNOWN'
  onClose: () => void
}) {
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', handleKey)
    return () => document.removeEventListener('keydown', handleKey)
  }, [onClose])

  const content = review.reviewReport?.trim()
  const fallback = review.selfReviewNotes?.trim()

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div
        className="absolute inset-0 bg-black/50"
        onClick={onClose}
      />
      <div className="relative z-10 w-[80vw] max-h-[90vh] bg-background rounded-lg shadow-xl flex flex-col">
        <div className="flex items-center justify-between px-6 py-4 border-b">
          <div className="flex items-center gap-3">
            <ResultBadge classified={classified} />
            <h3 className="text-base font-semibold text-foreground">Full Review Report</h3>
          </div>
          <Button
            variant="ghost"
            size="icon"
            onClick={onClose}
            className="rounded-md p-1 text-muted-foreground/70 hover:text-muted-foreground hover:bg-muted transition-colors"
          >
            <svg className="h-5 w-5" viewBox="0 0 20 20" fill="currentColor">
              <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
            </svg>
          </Button>
        </div>
        <div className="flex-1 overflow-y-auto px-6 py-4">
          {content ? (
            <div className="prose prose-sm max-w-none">
              <Markdown>{content}</Markdown>
            </div>
          ) : fallback ? (
            <div>
              <p className="text-sm text-muted-foreground mb-3">No detailed report available</p>
              <div className="prose prose-sm max-w-none">
                <Markdown>{fallback}</Markdown>
              </div>
            </div>
          ) : (
            <p className="text-sm text-muted-foreground">No detailed report available</p>
          )}
        </div>
      </div>
    </div>
  )
}