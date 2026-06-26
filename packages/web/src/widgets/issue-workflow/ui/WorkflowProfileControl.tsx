import { useEffect, useMemo, useState } from 'react'
import { useEffectiveDefaultWorkflowProfile, useWorkflowProfiles } from '../../../entities/settings'
import type { WorkflowProfileInfo } from '../../../entities/settings'
import {
  IssueStatus,
  useIssueWorkflowProfileYaml,
  useUpdateIssueWorkflowProfile,
} from '../../../entities/issue'
import type { Issue } from '../../../entities/issue'

interface WorkflowProfileControlProps {
  issue: Issue
}

const SYSTEM_DEFAULT_ID = 'mohist/local'

function isStartedIssue(issue: Issue): boolean {
  return issue.status !== IssueStatus.Backlog || !!issue.workflowRunId
}

export function WorkflowProfileControl({ issue }: WorkflowProfileControlProps) {
  const { data: workflowProfiles } = useWorkflowProfiles()
  const { data: workflowProfileYaml } = useIssueWorkflowProfileYaml(issue.number, true)
  const updateMutation = useUpdateIssueWorkflowProfile()
  const { effectiveTemplateId: defaultProfileId } = useEffectiveDefaultWorkflowProfile()

  const effectiveProfileId = issue.workflowProfileId
    ?? workflowProfileYaml?.profileId
    ?? defaultProfileId
    ?? SYSTEM_DEFAULT_ID

  const started = isStartedIssue(issue)
  const [pending, setPending] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setError(null)
  }, [effectiveProfileId])

  const profileOptions: WorkflowProfileInfo[] = useMemo(() => {
    const list = workflowProfiles ?? []
    const known = new Set(list.map((p) => p.id))
    const extras: WorkflowProfileInfo[] = []
    for (const id of [pending, defaultProfileId]) {
      if (!id || known.has(id)) continue
      known.add(id)
      extras.push({ id, displayName: id, description: '', isDefault: false })
    }
    return [...list, ...extras]
  }, [workflowProfiles, pending, defaultProfileId])

  const selectValue = pending ?? effectiveProfileId

  const lockedReason = started
    ? 'This issue has started — the execution template is locked to its current workflow run.'
    : null

  async function handleChange(value: string) {
    setError(null)
    const next = value || null
    setPending(next)
    try {
      await updateMutation.mutateAsync({ issueNumber: issue.number, workflowProfileId: next })
      setPending(null)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to update workflow profile')
      setPending(null)
    }
  }

  return (
    <div
      data-testid="issue-workflow-profile-control"
      data-effective-profile={effectiveProfileId}
      data-default-profile={defaultProfileId}
      className="rounded-lg border border-gray-200 bg-white p-4 space-y-2"
    >
      <div className="flex items-center justify-between gap-2">
        <h3 className="text-sm font-semibold text-gray-700">Workflow Profile</h3>
        <span
          data-testid="issue-workflow-profile-value"
          className="text-xs font-mono text-gray-600"
        >
          {effectiveProfileId}
        </span>
      </div>
      <p className="text-xs text-muted-foreground">
        Decides which workflow template runs when this issue is started.
        {workflowProfileYaml?.hasCustomTemplate && (
          <>
            {' '}A custom YAML override is active at runtime (separate concern).
          </>
        )}
      </p>
      <div className="flex items-center gap-2">
        <label className="sr-only" htmlFor={`issue-workflow-profile-select-${issue.number}`}>
          Workflow profile
        </label>
        <select
          id={`issue-workflow-profile-select-${issue.number}`}
          data-testid="issue-workflow-profile-select"
          value={selectValue}
          disabled={started || updateMutation.isPending}
          onChange={(e) => handleChange(e.target.value)}
          title={lockedReason ?? 'Change the workflow profile'}
          className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors disabled:opacity-60 disabled:cursor-not-allowed"
        >
          {profileOptions.length === 0 && <option value={SYSTEM_DEFAULT_ID}>{SYSTEM_DEFAULT_ID} (default)</option>}
          {profileOptions.map((p) => (
            <option key={p.id} value={p.id}>
              {p.displayName}
              {p.isDefault ? ' (default)' : ''}
            </option>
          ))}
        </select>
      </div>
      {started && lockedReason && (
        <p
          data-testid="issue-workflow-profile-locked-reason"
          className="text-xs text-amber-700"
        >
          {lockedReason}
        </p>
      )}
      {error && (
        <p
          data-testid="issue-workflow-profile-error"
          className="text-xs text-red-600"
        >
          {error}
        </p>
      )}
    </div>
  )
}
