import { useMutation } from '@tanstack/react-query'
import { useQueryClient } from '@tanstack/react-query'
import { Button } from '@/shared/ui/components/button'
import { IssueStatus, IssueHealth, WorkflowStage, startIssue } from '../../../entities/issue'
import type { Issue } from '../../../entities/issue'
import { useProject } from '../../../entities/project'
import { type DeliveryFailureKind } from '../../../shared/lib/delivery-failure'
import { CrossIcon } from './StageStatusIcons'

export function DeliveryFailureBanner({
  failureKind,
  label,
  nextAction,
  evidence,
  workspaceEvidence,
}: {
  failureKind: DeliveryFailureKind
  label: string
  nextAction: string
  evidence?: {
    expectedBranch: string
    observedBranch: string
    observedRef?: string | null
    boundary?: 'start' | 'end' | null
  } | null
  workspaceEvidence?: {
    workspacePath: string | null
  } | null
}) {
  const colors: Record<DeliveryFailureKind, string> = {
    conflict: 'border-danger-border bg-danger-subtle text-danger',
    'base-moved': 'border-warning-border bg-warning-subtle text-warning',
    'retry-safe': 'border-info-border bg-info-subtle text-info',
    'branch-invariant-violation': 'border-info-border bg-info-subtle text-info',
    'workspace-setup': 'border-danger-border bg-danger-subtle text-danger',
    'config-error': 'border-warning-border bg-warning-subtle text-warning',
    'protection-conflict': 'border-warning-border bg-warning-subtle text-warning',
    'pr-state-conflict': 'border-warning-border bg-warning-subtle text-warning',
  }
  const isWorkspaceSetup = failureKind === 'workspace-setup'
  return (
    <div className={`rounded-md border px-2.5 py-2 text-xs space-y-1 ${colors[failureKind]}`}>
      <div className="flex items-center gap-2 font-semibold">
        <span className="text-[10px] uppercase tracking-wide opacity-80">Failure kind</span>
        <span className="rounded bg-card/70 px-1.5 py-0.5 font-mono text-[11px]">{failureKind}</span>
        <span>{label}</span>
      </div>
      {failureKind === 'branch-invariant-violation' && (
        <div className="rounded bg-card/70 px-2 py-1 space-y-0.5 font-mono text-[11px]">
          <div className="text-[10px] uppercase tracking-wide opacity-80 font-sans">Attribution: runner/action (not issue work)</div>
          {evidence?.boundary && (
            <div>
              <span className="font-sans opacity-70">boundary:</span> {evidence.boundary}
            </div>
          )}
          <div>
            <span className="font-sans opacity-70">expected:</span>{' '}
            <span className="text-success">{evidence?.expectedBranch || '(unknown)'}</span>
          </div>
          <div>
            <span className="font-sans opacity-70">observed:</span>{' '}
            <span className="text-danger">
              {evidence?.observedBranch
                ? evidence.observedBranch
                : evidence?.observedRef
                  ? `(detached at ${evidence.observedRef})`
                  : '(unknown)'}
            </span>
          </div>
        </div>
      )}
      {isWorkspaceSetup && (
        <div className="rounded bg-card/70 px-2 py-1 space-y-0.5 font-mono text-[11px]">
          <div className="text-[10px] uppercase tracking-wide opacity-80 font-sans">
            Attribution: workflow infrastructure (not issue work)
          </div>
          {workspaceEvidence?.workspacePath && (
            <div>
              <span className="font-sans opacity-70">workspace:</span> {workspaceEvidence.workspacePath}
            </div>
          )}
        </div>
      )}
      <p className="leading-snug">{nextAction}</p>
    </div>
  )
}

export function IntegrateFailurePanel({ issue }: { issue: Issue }) {
  if (issue.workflowStage !== WorkflowStage.Integrate) return null
  if (issue.health !== IssueHealth.Blocked) return null

  const blockedReason = issue.blockedReason ?? 'Integration step failed'

  let failingStep = 'unknown'
  let capabilityOrFiles = ''
  let requirementHeader = ''
  let mergeReason = ''
  let healthCommand = ''
  let healthSummary = ''
  let healthLogExcerpt = ''
  let nextAction = 'Review the failure above and take action to resolve the issue.'

  if (blockedReason) {
    if (blockedReason.includes('archive')) {
      failingStep = 'Archive OpenSpec change'
      nextAction = 'Check disk space and permissions. Retry the archive step or return to Build.'
    } else if (blockedReason.includes('merge') || blockedReason.includes('Merge')) {
      failingStep = 'Merge to target branch'
      nextAction = 'Resolve any merge conflicts and return to Build for re-check.'
    } else if (blockedReason.includes('health') || blockedReason.includes('final-health')) {
      failingStep = 'Run final integration health check'
      nextAction = 'Review the health check failure and fix the underlying issue. Return to Build for re-check.'
    }
  }

  return (
    <div className="rounded-lg border border-danger-border bg-danger-subtle p-4 space-y-3">
      <div className="flex items-center gap-2">
        <CrossIcon className="h-4 w-4 text-danger" />
        <span className="text-sm font-semibold text-danger">Integration Failed</span>
      </div>
      <div className="space-y-1.5">
        <div className="text-xs text-danger">
          <span className="font-medium">Failing step:</span> {failingStep}
        </div>
        {capabilityOrFiles && (
          <div className="text-xs text-danger">
            <span className="font-medium">Affected:</span> {capabilityOrFiles}
          </div>
        )}
        {(requirementHeader || mergeReason) && (
          <div className="text-xs text-danger">
            {requirementHeader || mergeReason}
          </div>
        )}
        {(healthCommand || healthSummary) && (
          <div className="rounded border border-danger-border bg-card/70 p-2 space-y-1">
            {healthCommand && (
              <div className="text-xs font-mono text-danger">{healthCommand}</div>
            )}
            {healthSummary && (
              <div className="text-xs text-danger">{healthSummary}</div>
            )}
            {healthLogExcerpt && (
              <div className="text-xs text-danger mt-1 font-mono whitespace-pre-wrap">{healthLogExcerpt}</div>
            )}
          </div>
        )}
        <div className="pt-1 border-t border-danger-border">
          <p className="text-xs text-danger">{nextAction}</p>
        </div>
      </div>
    </div>
  )
}

export function SpecialStatePanel({
  issue,
  issueNumber,
}: {
  issue: Issue
  issueNumber: number
}) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()

  const startMutation = useMutation({
    mutationFn: () => startIssue(issueNumber, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
    },
  })

  if (issue.status === IssueStatus.Backlog) {
    return (
      <div className="flex justify-center py-4">
        <Button
          onClick={() => startMutation.mutate()}
          disabled={startMutation.isPending}
          className="px-6"
        >
          {startMutation.isPending ? 'Starting...' : 'Start'}
        </Button>
      </div>
    )
  }

  if (issue.health === IssueHealth.Blocked) {
    return (
      <div className="rounded-lg border border-danger-border bg-danger-subtle p-4 space-y-2">
        <div className="flex items-center gap-2">
          <CrossIcon className="h-4 w-4 text-danger" />
          <span className="text-sm font-semibold text-danger">Needs Action</span>
        </div>
        {issue.blockedReason && (
          <p className="text-sm text-danger">{issue.blockedReason}</p>
        )}
      </div>
    )
  }

  return null
}
