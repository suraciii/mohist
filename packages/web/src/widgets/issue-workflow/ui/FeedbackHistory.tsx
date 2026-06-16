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
            ? 'bg-green-500 border-green-600'
            : 'bg-amber-400 border-amber-500 animate-pulse'
        }`}
        aria-hidden="true"
      />
      {!isLast && (
        <span
          className="absolute left-[18px] top-5 bottom-0 w-px bg-gray-200"
          aria-hidden="true"
        />
      )}

      <div className="rounded-md border border-gray-200 bg-white p-3 space-y-2">
        <div className="flex items-center justify-between gap-2 flex-wrap">
          <div className="text-xs font-semibold text-gray-700">{cycleLabel}</div>
          <span
            className={`text-[10px] uppercase tracking-wide font-semibold rounded-full px-2 py-0.5 ${
              isResolved
                ? 'bg-green-50 text-green-700'
                : 'bg-amber-50 text-amber-700'
            }`}
          >
            {isResolved ? 'Resolved' : 'Awaiting application'}
          </span>
        </div>

        <ol className="space-y-2 text-xs text-gray-700">
          <li className="flex items-start gap-2">
            <span
              className="mt-1 h-1.5 w-1.5 rounded-full bg-blue-400 flex-shrink-0"
              aria-hidden="true"
            />
            <div>
              <span className="font-medium text-gray-800">Feedback requested</span>
              <span className="text-gray-500"> · {formatTime(feedback.createdAt)}</span>
              <div className="mt-1 rounded bg-gray-50 border border-gray-200 px-2 py-1.5 whitespace-pre-wrap break-words">
                {feedback.body}
              </div>
            </div>
          </li>

          {isResolved && feedback.resolution?.resolvedAt && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-purple-400 flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-gray-800">Feedback task applied</span>
                <span className="text-gray-500"> · {formatTime(feedback.resolution.resolvedAt)}</span>
                {feedback.resolution.resolutionTaskId && (
                  <span className="ml-2 text-[10px] text-gray-400 font-mono">
                    {feedback.resolution.resolutionTaskId}
                  </span>
                )}
              </div>
            </li>
          )}

          {isResolved && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-green-500 flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-gray-800">Resolution summary</span>
                {feedback.resolution?.resolutionSummary ? (
                  <div className="mt-1 rounded bg-green-50 border border-green-200 px-2 py-1.5 whitespace-pre-wrap break-words text-gray-700">
                    {feedback.resolution.resolutionSummary}
                  </div>
                ) : (
                  <div className="mt-1 text-gray-500 italic">No summary provided</div>
                )}
              </div>
            </li>
          )}

          {isOpen && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-amber-400 flex-shrink-0 animate-pulse"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-gray-800">Awaiting application</span>
                <span className="text-gray-500">
                  {' '}
                  · The apply-feedback task is pending
                </span>
              </div>
            </li>
          )}

          {hasNext && isResolved && (
            <li className="flex items-start gap-2">
              <span
                className="mt-1 h-1.5 w-1.5 rounded-full bg-blue-300 flex-shrink-0"
                aria-hidden="true"
              />
              <div>
                <span className="font-medium text-gray-800">
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
    <div className="rounded-md border border-gray-200 bg-gray-50 p-3 space-y-2">
      <div className="flex items-center justify-between gap-2 flex-wrap">
        <h4 className="text-xs font-semibold text-gray-700 uppercase tracking-wide">
          Feedback history
        </h4>
        <span className="text-[10px] text-gray-500" data-feedback-count={sortedFeedback.length}>
          {sortedFeedback.length} cycle{sortedFeedback.length !== 1 ? 's' : ''}
        </span>
      </div>

      {approvalRequestedAt && (
        <div className="text-[11px] text-gray-500">
          First approval requested at {formatTime(approvalRequestedAt)}
        </div>
      )}

      {checks && checks.length > 0 && allResolved && (
        <div className="text-[11px] text-gray-500">
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
        <div className="rounded bg-amber-50 border border-amber-200 px-2 py-1.5 text-[11px] text-amber-700">
          The agent is applying your feedback. The stage will return to approval
          once checks rerun.
        </div>
      )}
    </div>
  )
}
