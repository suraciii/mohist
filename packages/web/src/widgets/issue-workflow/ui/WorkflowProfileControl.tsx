import { useEffect, useMemo, useState } from 'react'
import { useEffectiveDefaultWorkflowProfile, useWorkflowProfiles } from '../../../entities/settings'
import type { WorkflowProfileInfo } from '../../../entities/settings'
import {
  IssueStatus,
  useIssueWorkflowProfileYaml,
  useUpdateIssueWorkflowProfile,
} from '../../../entities/issue'
import type { Issue } from '../../../entities/issue'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/shared/ui/components/select'

interface WorkflowProfileControlProps {
  issue: Issue
  embedded?: boolean
  dataHook?: WorkflowProfileControlDataHook
}

interface WorkflowProfileUpdateMutation {
  mutateAsync: (variables: { issueNumber: number; workflowProfileId: string | null }) => Promise<unknown>
  isPending: boolean
}

export interface WorkflowProfileControlData {
  workflowProfiles: WorkflowProfileInfo[] | undefined
  workflowProfileYaml: { profileId?: string | null; hasCustomTemplate?: boolean } | null | undefined
  updateMutation: WorkflowProfileUpdateMutation
  defaultProfileId: string | null | undefined
}

export type WorkflowProfileControlDataHook = (issueNumber: number) => WorkflowProfileControlData

const useDefaultData: WorkflowProfileControlDataHook = (issueNumber) => {
  const { data: workflowProfiles } = useWorkflowProfiles()
  const { data: workflowProfileYaml } = useIssueWorkflowProfileYaml(issueNumber, true)
  const updateMutation = useUpdateIssueWorkflowProfile()
  const { effectiveTemplateId: defaultProfileId } = useEffectiveDefaultWorkflowProfile()
  return { workflowProfiles, workflowProfileYaml, updateMutation, defaultProfileId }
}

const SYSTEM_DEFAULT_ID = 'mohist/local'

function isStartedIssue(issue: Issue): boolean {
  return issue.status !== IssueStatus.Backlog || !!issue.workflowRunId
}

export function WorkflowProfileControl({
  issue,
  embedded = false,
  dataHook = useDefaultData,
}: WorkflowProfileControlProps) {
  const {
    workflowProfiles,
    workflowProfileYaml,
    updateMutation,
    defaultProfileId,
  } = dataHook(issue.number)

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
      className={embedded ? 'space-y-2' : 'rounded-lg border border-border bg-card p-4 space-y-2'}
    >
      <div className="flex items-center justify-between gap-2">
        {!embedded && <h3 className="text-sm font-semibold text-card-foreground">Workflow Profile</h3>}
        <span
          data-testid="issue-workflow-profile-value"
          className={embedded ? 'text-xs font-mono text-foreground' : 'text-xs font-mono text-muted-foreground'}
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
        <Select
          value={selectValue}
          onValueChange={(value) => {
            if (value !== null) void handleChange(value)
          }}
          disabled={started || updateMutation.isPending}
        >
          <SelectTrigger
            id={`issue-workflow-profile-select-${issue.number}`}
            data-testid="issue-workflow-profile-select"
            title={lockedReason ?? 'Change the workflow profile'}
            className="h-9 w-full"
          >
            <SelectValue>
              {profileOptions.find((profile) => profile.id === selectValue)?.displayName ?? SYSTEM_DEFAULT_ID}
            </SelectValue>
          </SelectTrigger>
          <SelectContent>
            {profileOptions.length === 0 && <SelectItem value={SYSTEM_DEFAULT_ID}>{SYSTEM_DEFAULT_ID} (default)</SelectItem>}
            {profileOptions.map((p) => (
              <SelectItem key={p.id} value={p.id}>
                {p.displayName}
                {p.isDefault ? ' (default)' : ''}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      {started && lockedReason && (
        <p
          data-testid="issue-workflow-profile-locked-reason"
          className="text-xs text-warning"
        >
          {lockedReason}
        </p>
      )}
      {error && (
        <p
          data-testid="issue-workflow-profile-error"
          className="text-xs text-danger"
        >
          {error}
        </p>
      )}
    </div>
  )
}
