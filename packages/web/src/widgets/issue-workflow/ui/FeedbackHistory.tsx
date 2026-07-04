import { useMemo } from 'react'
import type { ApprovalFeedback, StageCheckState, StageStateStatus, WorkflowStage } from '../../../entities/issue'

export interface FeedbackHistoryProps {
  stage: WorkflowStage
  feedback: ApprovalFeedback[]
  approvalRequestedAt?: string | null
  checks?: StageCheckState[]
  stageStatus?: StageStateStatus
}

function formatTime(at: string): string {
  if (!at) return ''
  const parsed = new Date(at)
  if (Number.isNaN(parsed.getTime())) return at
  return parsed.toLocaleString()
}

function FeedbackCycle({
  feedback,
  index,
  hasNext,
  isLast,
}: {
  feedback: ApprovalFeedback
  index: number
  hasNext: boolean
  isLast: boolean
}) {
  const isOpen = feedback.status === 'open'
  const isResolved = feedback.status === 'resolved'
  const cycleLabel = `Cycle ${index + 1}`

  return (
    <li
      className={`relative pl-7 ${isOpen ? 'pb-3' : 'pb-4'}`}
      data-testid={`feedback-${feedback.id}`}
      data-feedback-id={feedback.id}
      data-feedback-status={feedback.status}
    >
      <span
        className={`absolute left-1.5 top-1.5 h-3 w-3 rounded-full border-2 ${
          isResolved
            ? 'bg-success border-success'
            : 'bg-warning border-warning animate-pulse'
        }`}
        aria-hidden="true"
      />
      {!isLast && (
        <span
          className="absolute left-[18px] top-5 bottom-0 w-px bg-border"
          aria-hidden="true"
        />
      )}

      <div className="rounded-md border border-border bg-card p-3 space-y-2">
        <div className="flex items-center justify-between gap-2 flex-wrap">
          <div className="text-xs font-semibold text-card-foreground">{cycleLabel}</div>
          <span
            className={`text-[10px] uppercase tracking-wide font-semibold rounded-full px-2 py-0.5 ${
              isResolved
                ? 'bg-success-subtle text-success'
                : 'bg-warning-subtle text-warning'
            }`}
          >
            {isResolved ? 'Resolved' : 'Awaiting application'}
          </span>
        </div>

        <ol className="space-y-2 text-xs text-muted-foreground">
          <li className="flex items-start gap-2">
            <span
              className="mt-1 h-1.5 w-1.5 rounded-full bg-info flex-shrink-0"
              aria-hidden="true"
            />
            <div>
              <span className="font-medium text-card-foreground">Feedback requested</span>
              <span className="text-muted-foreground"> · {formatTime(feedback.createdAt)}</span>
              <div className="mt-1 rounded bg-muted border border-border px-2 py-1.5 whitespace-pre-wrap break-words">
                {feedback.body}
              </div>
            </div>
          </li>

          {isResolved && feedback.resolution?.resolvedAt && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-info/70 flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-card-foreground">Feedback task applied</span>
                <span className="text-muted-foreground"> · {formatTime(feedback.resolution.resolvedAt)}</span>
                {feedback.resolution.resolutionTaskId && (
                  <span className="ml-2 text-[10px] text-muted-foreground/70 font-mono">
                    {feedback.resolution.resolutionTaskId}
                  </span>
                )}
              </div>
            </li>
          )}

          {isResolved && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-success flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-card-foreground">Resolution summary</span>
                {feedback.resolution?.resolutionSummary ? (
                  <div className="mt-1 rounded bg-success-subtle border border-success-border px-2 py-1.5 whitespace-pre-wrap break-words text-success">
                    {feedback.resolution.resolutionSummary}
                  </div>
                ) : (
                  <div className="mt-1 text-muted-foreground italic">No summary provided</div>
                )}
              </div>
            </li>
          )}

          {isOpen && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-warning flex-shrink-0 animate-pulse"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-card-foreground">Awaiting application</span>
                <span className="text-muted-foreground">
                  {' '}
                  · The apply-feedback task is pending
                </span>
              </div>
            </li>
          )}

          {hasNext && isResolved && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-info/60 flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-card-foreground">
                  Next approval requested
                </span>
              </div>
            </li>
          )}
        </ol>
      </div>
    </li>
  )
}

export function FeedbackHistory({
  feedback,
  approvalRequestedAt,
  checks,
}: FeedbackHistoryProps) {
  const sortedFeedback = useMemo(
    () =>
      [...feedback].sort((a, b) => {
        const aTime = new Date(a.createdAt).getTime() || 0
        const bTime = new Date(b.createdAt).getTime() || 0
        return aTime - bTime
      }),
    [feedback],
  )

  if (sortedFeedback.length === 0) return null

  const lastFeedback = sortedFeedback[sortedFeedback.length - 1]
  const allResolved = sortedFeedback.every((f) => f.status === 'resolved')

  return (
    <div className="rounded-md border border-border bg-muted p-3 space-y-2">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h4 className="text-xs font-semibold text-card-foreground uppercase tracking-wide">
          Feedback history
        </h4>
        <span className="text-[10px] text-muted-foreground" data-feedback-count={sortedFeedback.length}>
          {sortedFeedback.length} cycle{sortedFeedback.length !== 1 ? 's' : ''}
        </span>
      </div>

      {approvalRequestedAt && (
        <div className="text-[11px] text-muted-foreground">
          First approval requested at {formatTime(approvalRequestedAt)}
        </div>
      )}

      {checks && checks.length > 0 && allResolved && (
        <div className="text-[11px] text-muted-foreground">
          Latest check rerun: {checks.map((c) => `${c.checkName} (${c.status})`).join(', ')}
        </div>
      )}

      <ol className="space-y-0">
        {sortedFeedback.map((item, idx) => (
          <FeedbackCycle
            key={item.id}
            feedback={item}
            index={idx}
            hasNext={idx < sortedFeedback.length - 1}
            isLast={idx === sortedFeedback.length - 1}
          />
        ))}
      </ol>

      {lastFeedback && lastFeedback.status === 'open' && (
        <div className="rounded bg-warning-subtle border border-warning-border px-2 py-1.5 text-[11px] text-warning">
          The agent is applying your feedback. The stage will return to approval
          once checks rerun.
        </div>
      )}
    </div>
  )
}
