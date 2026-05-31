import type { PlanProgress } from '../../coder-session/model/useSessionTimeline'

function formatStepDuration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`
  const h = Math.floor(seconds / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = Math.round(seconds % 60)
  if (h > 0) return `${h}h ${m}m`
  return `${m}m ${s}s`
}

function PlanStepStatusIcon({ status }: { status: string }) {
  switch (status) {
    case 'completed':
      return (
        <svg className="h-3.5 w-3.5 text-green-500" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zm3.857-9.809a.75.75 0 00-1.214-.882l-3.483 4.79-1.88-1.88a.75.75 0 10-1.06 1.061l2.5 2.5a.75.75 0 001.137-.089l4-5.5z" clipRule="evenodd" />
        </svg>
      )
    case 'running':
      return (
        <svg className="h-3.5 w-3.5 text-blue-500 animate-spin" viewBox="0 0 24 24" fill="none">
          <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
          <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
        </svg>
      )
    case 'failed':
      return (
        <svg className="h-3.5 w-3.5 text-red-500" viewBox="0 0 20 20" fill="currentColor">
          <path fillRule="evenodd" d="M10 18a8 8 0 100-16 8 8 0 000 16zM8.28 7.22a.75.75 0 00-1.06 1.06L8.94 10l-1.72 1.72a.75.75 0 101.06 1.06L10 11.06l1.72 1.72a.75.75 0 101.06-1.06L11.06 10l1.72-1.72a.75.75 0 00-1.06-1.06L10 8.94 8.28 7.22z" clipRule="evenodd" />
        </svg>
      )
    default:
      return (
        <span className="inline-block h-2.5 w-2.5 rounded-full bg-gray-300" />
      )
  }
}

export function PlanProgressPanel({ planProgress }: { planProgress: PlanProgress }) {
  const { steps, completedCount, totalSteps } = planProgress

  return (
    <div className="rounded-lg border border-blue-200 bg-blue-50/30 p-3 mb-2">
      <div className="flex items-center justify-between mb-2">
        <span className="text-xs font-medium text-blue-800">Plan Progress</span>
        <span className="text-xs text-blue-600">{completedCount} / {totalSteps} completed</span>
      </div>
      <div className="space-y-1.5">
        {steps.map((step) => (
          <div key={`${step.roundType}-${step.roundIndex}`} className="flex items-center gap-2">
            <PlanStepStatusIcon status={step.status} />
            <span className="text-xs font-mono text-gray-700">{step.roundLabel}</span>
            {step.verdict && (
              <span className={`text-xs font-semibold ${step.verdict === 'PASS' ? 'text-green-600' : 'text-red-600'}`}>
                — {step.verdict}
              </span>
            )}
            {step.status === 'completed' && step.duration != null && (
              <span className="text-xs text-gray-400 ml-auto">{formatStepDuration(step.duration)}</span>
            )}
            {step.status === 'failed' && step.duration != null && (
              <span className="text-xs text-gray-400 ml-auto">{formatStepDuration(step.duration)}</span>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
