import { useState, useMemo, useEffect, useRef } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { AttachmentComposer } from '@/shared/ui'
import {
  createIssue,
  extractAttachmentIds,
  IssuePrerequisitePicker,
  issueCandidateKeys,
  issueDetailKeys,
  issueListKeys,
  LabelEditor,
  partitionIssueBody,
  useParentIssueCandidates,
} from '../../../entities/issue'
import type { Issue, LabelMap } from '../../../entities/issue'
import { getWorkflowProfileAgentRuntime, useAvailableModelIds, useEffectiveDefaultWorkflowProfile, useWorkflowProfiles } from '../../../entities/settings'
import type { AgentRuntime, WorkflowProfileInfo } from '../../../entities/settings'
import { useIssueTemplate, useIssueTemplates } from '../../../entities/issue-templates'
import { useProject, useRepositories } from '../../../entities/project'
import { getPriorityStyle, getRiskStyle } from '../../../shared/lib/label-colors'
import {
  mapCreateIssueError,
  pickInitialRepositoryName,
} from '../lib/assignment'
import { ModelSelect } from '../../../shared/ui/ModelSelect'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']
const RISKS = ['low', 'medium', 'high']

interface Props {
  open: boolean
  onClose: () => void
}

function ModelPresetSelect({
  runtime,
  value,
  variant,
  onChange,
  onVariantChange,
  onClear,
}: {
  runtime: AgentRuntime | null
  value: string | null
  variant: string | null
  onChange: (id: string) => void
  onVariantChange: (variant: string | null) => void
  onClear: () => void
}) {
  const { data: availableModels } = useAvailableModelIds(runtime)
  const allModels: string[] = availableModels?.models ?? []
  const modelVariantsMap = availableModels?.modelVariants ?? {}
  const availableVariants = value ? modelVariantsMap[value] ?? [] : []
  const resolvedVariant = variant && availableVariants.includes(variant) ? variant : null
  const readOnly = runtime === null

  return (
    <div>
      <ModelSelect
        id="create-issue-model-trigger"
        value={value}
        placeholder="Use default"
        models={readOnly ? [] : allModels}
        onChange={(modelId) => {
          onChange(modelId)
          onVariantChange(null)
        }}
        onClear={() => {
          onClear()
          onVariantChange(null)
        }}
        allowClear={!!value}
        modelVariants={modelVariantsMap}
        valueVariant={resolvedVariant}
        onChangeModelVariant={(modelId, chipVariant) => {
          onChange(modelId)
          onVariantChange(chipVariant)
        }}
        disabled={readOnly}
      />
    </div>
  )
}

function TemplateSelector({
  templates,
  isLoading,
  value,
  onChange,
}: {
  templates: Array<{ id: string; name: string; description: string; source: 'builtin' | 'custom' }>
  isLoading: boolean
  value: string | null
  onChange: (id: string | null) => void
}) {
  const options = useMemo(() => {
    const list = templates
    const known = new Set(list.map((t) => t.id))
    const extras = value && !known.has(value)
      ? [{ id: value, name: value, description: '', source: 'custom' as const }]
      : []
    return [...list, ...extras]
  }, [templates, value])

  return (
    <div>
      <label className="block text-xs font-medium text-foreground mb-1">Template</label>
      <select
        aria-label="Template"
        data-testid="issue-template-selector"
        value={value ?? ''}
        onChange={(e) => onChange(e.target.value === '' ? null : e.target.value)}
        className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors"
        disabled={isLoading}
      >
        <option value="">{isLoading ? 'Loading templates…' : 'No template'}</option>
        {options.map((t) => (
          <option key={t.id} value={t.id}>
            {t.name}
            {t.source === 'custom' ? ' (custom)' : ''}
          </option>
        ))}
      </select>
    </div>
  )
}

export function CreateIssueDialog(props: Props) {
  if (!props.open) return null
  return <CreateIssueDialogContent {...props} />
}

function CreateIssueDialogContent({ open, onClose }: Props) {
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [labels, setLabels] = useState<LabelMap>({})
  const [model, setModel] = useState<string | null>(null)
  const [modelVariant, setModelVariant] = useState<string | null>(null)
  const [priority, setPriority] = useState<string>('p2')
  const [repositoryName, setRepositoryName] = useState<string | null>(null)
  const [parentIssueNumber, setParentIssueNumber] = useState<number | null>(null)
  const [workflowProfileId, setWorkflowProfileId] = useState<string | null>(null)
  const [workflowTouched, setWorkflowTouched] = useState(false)
  const [risk, setRisk] = useState<string | null>(null)
  const [riskTouched, setRiskTouched] = useState(false)
  const [selectedTemplateId, setSelectedTemplateId] = useState<string | null>(null)
  const [prerequisiteNumbers, setPrerequisiteNumbers] = useState<number[]>([])
  const [assignmentErrorMessage, setAssignmentErrorMessage] = useState<string | null>(null)
  const { projectId, projects } = useProject()
  const currentProject = projects?.find((p) => p.id === projectId)
  const { data: repositories } = useRepositories(currentProject?.id)
  const { data: workflowProfiles } = useWorkflowProfiles()
  const { data: issueTemplates, isLoading: issueTemplatesLoading } = useIssueTemplates()
  const { data: selectedTemplate } = useIssueTemplate(selectedTemplateId)
  const queryClient = useQueryClient()

  const frontmatter = useMemo(() => partitionIssueBody(body), [body])
  const recommendation = useMemo(() => {
    if (frontmatter.kind === 'closed' && frontmatter.recommendedWorkflow) {
      return {
        workflow: frontmatter.recommendedWorkflow,
        reason: frontmatter.recommendedWorkflowReason ?? null,
      }
    }
    return null
  }, [frontmatter])
  const frontmatterRisk = frontmatter.kind === 'closed' ? frontmatter.risk ?? null : null

  const lastInitializedProjectIdRef = useRef<string | null>(null)
  useEffect(() => {
    if (!projectId) return
    if (lastInitializedProjectIdRef.current !== projectId) {
      setRepositoryName(null)
      setParentIssueNumber(null)
      lastInitializedProjectIdRef.current = projectId
    }
    if (repositories !== undefined) {
      setRepositoryName((current) => pickInitialRepositoryName(repositories, current))
    }
  }, [projectId, repositories])

  const parentCandidatesQuery = useParentIssueCandidates()
  const parentIssuesLoaded = parentCandidatesQuery.data !== undefined
  const eligibleParentCandidates = parentCandidatesQuery.data ?? []
  useEffect(() => {
    if (parentIssueNumber === null) return
    if (!parentIssuesLoaded) return
    if (!eligibleParentCandidates.some((issue) => issue.number === parentIssueNumber)) {
      setParentIssueNumber(null)
    }
  }, [eligibleParentCandidates, parentIssueNumber, parentIssuesLoaded])

  const enabledWorkflowIds = useMemo(
    () => new Set((workflowProfiles ?? []).map((profile) => profile.id)),
    [workflowProfiles],
  )
  const recommendationIsEnabled = recommendation
    ? enabledWorkflowIds.has(recommendation.workflow)
    : false
  const recommendationUnavailable = recommendation && !recommendationIsEnabled
  const recommendedWorkflowProfileId = recommendation && recommendationIsEnabled && !workflowTouched
    ? recommendation.workflow
    : null
  const { effectiveTemplateId: defaultProfileId } = useEffectiveDefaultWorkflowProfile()
  const submittedWorkflowProfileId = workflowTouched
    ? workflowProfileId
    : recommendedWorkflowProfileId ?? defaultProfileId ?? null
  const effectiveRisk = riskTouched ? risk : frontmatterRisk

  useEffect(() => {
    if (selectedTemplate) {
      setBody(selectedTemplate.body)
    }
  }, [selectedTemplate])

  const workflowSelectValue = workflowTouched
    ? workflowProfileId ?? ''
    : recommendedWorkflowProfileId ?? defaultProfileId ?? ''
  const selectedWorkflowRuntime = getWorkflowProfileAgentRuntime(workflowProfiles, workflowSelectValue || null)

  const mutation = useMutation({
    mutationFn: () => {
      setAssignmentErrorMessage(null)
      return createIssue({
        title,
        body: body || undefined,
        attachmentIds: extractAttachmentIds(body),
        labels: Object.keys(labels).length > 0 ? labels : undefined,
        ...(model ? { model } : {}),
        ...(modelVariant ? { modelVariant } : {}),
        agentConfig: { ...(model ? { model } : {}), ...(modelVariant ? { variant: modelVariant } : {}) },
        ...(projectId ? { projectId } : {}),
        priority,
        ...(repositoryName ? { repositoryName } : {}),
        ...(submittedWorkflowProfileId ? { workflowProfileId: submittedWorkflowProfileId } : {}),
        ...(effectiveRisk ? { risk: effectiveRisk } : {}),
        ...(prerequisiteNumbers.length > 0 ? { prerequisiteNumbers } : {}),
        ...(parentIssueNumber != null ? { parentIssueNumber } : {}),
      })
    },
    onSuccess: (data: Issue) => {
      toast.success(`Issue #${data.number} created`)
      if (parentIssueNumber != null) {
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, parentIssueNumber), exact: true })
      }
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      queryClient.invalidateQueries({ queryKey: issueCandidateKeys.project(projectId), exact: true })
      resetAndClose()
    },
    onError: (err: Error) => {
      const mapped = mapCreateIssueError(err)
      if (mapped.isAssignment) {
        setAssignmentErrorMessage(mapped.message)
        return
      }
      toast.error(mapped.message)
    },
  })

  function resetAndClose() {
    setTitle('')
    setBody('')
    setLabels({})
    setModel(null)
    setModelVariant(null)
    setPriority('p2')
    setRepositoryName(null)
    setParentIssueNumber(null)
    setWorkflowProfileId(null)
    setWorkflowTouched(false)
    setRisk(null)
    setRiskTouched(false)
    setSelectedTemplateId(null)
    setPrerequisiteNumbers([])
    setAssignmentErrorMessage(null)
    onClose()
  }

  const profileOptions: WorkflowProfileInfo[] = workflowProfiles ?? []
  return (
    <Dialog open={open} onOpenChange={(v) => !v && resetAndClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Create Issue</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Title *</label>
            <Input
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              placeholder="Issue title"
              autoFocus
            />
          </div>

          <TemplateSelector
            templates={issueTemplates ?? []}
            isLoading={issueTemplatesLoading}
            value={selectedTemplateId}
            onChange={setSelectedTemplateId}
          />

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Description</label>
            <AttachmentComposer
              projectId={currentProject?.id ?? projectId ?? ''}
              value={body}
              onChange={setBody}
              placeholder="Optional description"
              rows={3}
              className="resize-none"
            />
          </div>

          {recommendation && (
            <div
              data-testid="workflow-recommendation"
              className="rounded-md border border-blue-200 bg-blue-50 px-3 py-2"
            >
              <div className="text-xs font-medium text-blue-800">
                Recommended workflow:{' '}
                <span className="font-semibold" data-testid="recommended-workflow">
                  {recommendation.workflow}
                </span>
              </div>
              {recommendation.reason && (
                <p
                  className="text-xs text-blue-700 mt-1 whitespace-pre-wrap"
                  data-testid="recommended-workflow-reason"
                >
                  {recommendation.reason}
                </p>
              )}
              {recommendationUnavailable ? (
                <p className="text-[11px] text-amber-700 mt-1" data-testid="workflow-recommendation-unavailable">
                  This workflow is not enabled for the current project. Choose an enabled workflow or use the project default.
                </p>
              ) : (
                <p className="text-[11px] text-blue-600/80 mt-1">
                  Pre-filled below. Change the selector to override.
                </p>
              )}
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Workflow</label>
            <select
              aria-label="Workflow"
              value={workflowSelectValue}
              onChange={(e) => {
                setWorkflowProfileId(e.target.value || null)
                setWorkflowTouched(true)
              }}
              className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors"
            >
              {profileOptions.length === 0 && <option value="">Default</option>}
              {profileOptions.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.displayName}
                  {p.isDefault ? ' (default)' : ''}
                </option>
              ))}
            </select>
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Risk</label>
            <div className="flex gap-1.5" role="group" aria-label="Risk">
              {RISKS.map((r) => {
                const style = getRiskStyle(r)
                return (
                  <Button
                    key={r}
                    type="button"
                    variant="ghost"
                    size="xs"
                    aria-pressed={effectiveRisk === r}
                    onClick={() => {
                      setRisk(r)
                      setRiskTouched(true)
                    }}
                    className={`rounded-full capitalize ${
                      effectiveRisk === r ? 'ring-1 ring-offset-1' : 'hover:opacity-80'
                    }`}
                    style={{
                      backgroundColor: style.bg,
                      color: style.text,
                      ...(effectiveRisk === r ? { ringColor: style.text } : {}),
                    }}
                  >
                    {r}
                  </Button>
                )
              })}
            </div>
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Labels</label>
            <LabelEditor
              value={labels}
              onChange={setLabels}
              inputIdPrefix="create-issue-label"
              emptyHint="Add a key+value pair (e.g. stream=frontend) to classify this issue."
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Prerequisites</label>
            <IssuePrerequisitePicker
              projectId={projectId ?? ''}
              selected={prerequisiteNumbers}
              excludeNumbers={prerequisiteNumbers}
              mode="buffer"
              onAdd={(n) => setPrerequisiteNumbers((prev) => (prev.includes(n) ? prev : [...prev, n]))}
              onRemove={(n) => setPrerequisiteNumbers((prev) => prev.filter((x) => x !== n))}
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Repository</label>
            {repositories === undefined && (
              <div
                data-testid="create-issue-repository-loading"
                className="rounded-md border border-dashed border-input bg-muted/20 px-3 py-2 text-sm text-muted-foreground"
              >
                Loading repository…
              </div>
            )}
            {repositories && repositories.length > 1 && (
              <select
                aria-label="Repository"
                data-testid="create-issue-repository-select"
                value={repositoryName ?? ''}
                onChange={(e) => setRepositoryName(e.target.value || null)}
                className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors"
              >
                {repositories.map((repo) => (
                  <option key={repo.name} value={repo.name}>
                    {repo.name} {repo.isDefault ? '(default)' : ''}
                  </option>
                ))}
              </select>
            )}
            {repositories && repositories.length === 1 && (
              <div
                data-testid="create-issue-repository-label"
                className="rounded-md border border-input bg-muted/30 px-3 py-2 text-sm text-muted-foreground"
              >
                {repositories[0].name} (only repository)
              </div>
            )}
            {repositories && repositories.length === 0 && (
              <div
                data-testid="create-issue-repository-empty"
                className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-700"
              >
                No repositories declared for this project.
              </div>
            )}
          </div>

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Parent issue</label>
            <select
              aria-label="Parent issue"
              data-testid="create-issue-parent-select"
              value={parentIssueNumber != null ? String(parentIssueNumber) : ''}
              onChange={(e) => setParentIssueNumber(e.target.value === '' ? null : Number(e.target.value))}
              className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm transition-colors"
              disabled={parentCandidatesQuery.isLoading}
            >
              <option value="">
                {parentCandidatesQuery.isLoading ? 'Loading issues…' : 'No parent (ordinary issue)'}
              </option>
              {eligibleParentCandidates.map((issue) => (
                <option key={issue.number} value={issue.number}>
                  #{issue.number} · {issue.title}
                </option>
              ))}
            </select>
          </div>

          {(selectedWorkflowRuntime !== null || model) && (
            <div>
              <label className="block text-xs font-medium text-foreground mb-1">Coder Model</label>
              <ModelPresetSelect
                runtime={selectedWorkflowRuntime}
                value={model}
                variant={modelVariant}
                onChange={setModel}
                onVariantChange={setModelVariant}
                onClear={() => setModel(null)}
              />
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Priority</label>
            <div className="flex gap-1.5">
              {PRIORITIES.map((p) => {
                const style = getPriorityStyle(p)
                return (
                  <Button
                    key={p}
                    type="button"
                    variant="ghost"
                    size="xs"
                    onClick={() => setPriority(p)}
                    className={`rounded-full ${
                      priority === p
                        ? 'ring-1 ring-offset-1'
                        : 'hover:opacity-80'
                    }`}
                    style={{
                      backgroundColor: style.bg,
                      color: style.text,
                      ...(priority === p ? { ringColor: style.text } : {}),
                    }}
                  >
                    {p.toUpperCase()}
                  </Button>
                )
              })}
            </div>
          </div>

          {assignmentErrorMessage && (
            <div
              data-testid="create-issue-assignment-error"
              className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600"
              role="alert"
            >
              {assignmentErrorMessage}
            </div>
          )}

          {mutation.error && !assignmentErrorMessage && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {mutation.error.message}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <Button
              variant="outline"
              onClick={resetAndClose}
            >
              Cancel
            </Button>
            <Button
              onClick={() => mutation.mutate()}
              disabled={!title.trim() || mutation.isPending}
              className="min-h-[44px]"
            >
              {mutation.isPending ? 'Creating...' : 'Create'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
