import { useState, useMemo, useEffect } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { AttachmentComposer } from '@/shared/ui'
import { createIssue, extractAttachmentIds } from '../../../entities/issue'
import { LabelEditor } from '../../../entities/issue/lib/label-editor'
import type { LabelMap } from '../../../entities/issue/model/labels'
import { useAvailableModelIds, useEffectiveDefaultWorkflowProfile, useWorkflowProfiles } from '../../../entities/settings'
import type { WorkflowProfileInfo } from '../../../entities/settings'
import { composeIssueTemplateBody, useIssueTemplate, useIssueTemplates } from '../../../entities/issue-templates'
import { useProject, useRepositories } from '../../../entities/project'
import { getPriorityStyle, getRiskStyle } from '../../../shared/lib/label-colors'
import { parseIssueFrontmatter } from '../lib/frontmatter'
import { ModelSelect } from '../../../shared/ui/ModelSelect'

const PRIORITIES = ['p0', 'p1', 'p2', 'p3', 'p4']
const RISKS = ['low', 'medium', 'high']

interface Props {
  open: boolean
  onClose: () => void
}

function ModelPresetSelect({
  value,
  variant,
  onChange,
  onVariantChange,
  onClear,
}: {
  value: string | null
  variant: string | null
  onChange: (id: string) => void
  onVariantChange: (variant: string | null) => void
  onClear: () => void
}) {
  const { data: availableModels } = useAvailableModelIds()
  const allModels: string[] = availableModels?.models ?? []
  const modelVariantsMap = availableModels?.modelVariants ?? {}
  const availableVariants = value ? modelVariantsMap[value] ?? [] : []
  const resolvedVariant = variant && availableVariants.includes(variant) ? variant : null

  return (
    <div>
      <ModelSelect
        id="create-issue-model-trigger"
        value={value}
        placeholder="Use default"
        models={allModels}
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
  templates: Array<{ id: string; name: string; about: string; isDefault: boolean; source: 'builtin' | 'custom' }>
  isLoading: boolean
  value: string | null
  onChange: (id: string | null) => void
}) {
  const options = useMemo(() => {
    const list = templates
    const known = new Set(list.map((t) => t.id))
    const extras = value && !known.has(value)
      ? [{ id: value, name: value, about: '', isDefault: false, source: 'custom' as const }]
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
            {t.isDefault ? ' (default)' : ''}
            {t.source === 'custom' ? ' (custom)' : ''}
          </option>
        ))}
      </select>
    </div>
  )
}

export function CreateIssueDialog({ open, onClose }: Props) {
  const [title, setTitle] = useState('')
  const [body, setBody] = useState('')
  const [labels, setLabels] = useState<LabelMap>({})
  const [model, setModel] = useState<string | null>(null)
  const [modelVariant, setModelVariant] = useState<string | null>(null)
  const [priority, setPriority] = useState<string>('p2')
  const [repositoryName, setRepositoryName] = useState<string | null>(null)
  const [workflowProfileId, setWorkflowProfileId] = useState<string | null>(null)
  const [workflowTouched, setWorkflowTouched] = useState(false)
  const [risk, setRisk] = useState<string | null>(null)
  const [riskTouched, setRiskTouched] = useState(false)
  const [selectedTemplateId, setSelectedTemplateId] = useState<string | null>(null)
  const { projectId, projects } = useProject()
  const currentProject = projects?.find((p) => p.id === projectId)
  const { data: repositories } = useRepositories(currentProject?.id)
  const { data: workflowProfiles } = useWorkflowProfiles()
  const { data: issueTemplates, isLoading: issueTemplatesLoading } = useIssueTemplates()
  const { data: selectedTemplate } = useIssueTemplate(selectedTemplateId)
  const queryClient = useQueryClient()

  const frontmatter = useMemo(() => parseIssueFrontmatter(body), [body])
  const recommendation = useMemo(() => {
    if (frontmatter.kind === 'parsed' && frontmatter.recommendedWorkflow) {
      return {
        workflow: frontmatter.recommendedWorkflow,
        reason: frontmatter.recommendedWorkflowReason ?? null,
      }
    }
    return null
  }, [frontmatter])
  const frontmatterRisk =
    frontmatter.kind === 'parsed' ? frontmatter.risk ?? null : null

  useEffect(() => {
    if (repositories && repositories.length === 1) {
      setRepositoryName(repositories[0].name)
    }
  }, [repositories])

  useEffect(() => {
    if (recommendation && !workflowTouched) {
      setWorkflowProfileId(recommendation.workflow)
    }
  }, [recommendation, workflowTouched])

  useEffect(() => {
    if (frontmatterRisk && !riskTouched) {
      setRisk(frontmatterRisk)
    }
  }, [frontmatterRisk, riskTouched])

  useEffect(() => {
    if (selectedTemplate) {
      setBody(composeIssueTemplateBody(selectedTemplate))
    }
  }, [selectedTemplate])

  const mutation = useMutation({
    mutationFn: () =>
      createIssue({
        title,
        body: body || undefined,
        attachmentIds: extractAttachmentIds(body),
        labels: Object.keys(labels).length > 0 ? labels : undefined,
        ...(model ? { model } : {}),
        ...(modelVariant ? { modelVariant } : {}),
        ...(projectId ? { projectId } : {}),
        priority,
        ...(repositoryName ? { repositoryName } : {}),
        ...(workflowProfileId ? { workflowProfileId } : {}),
        ...(risk ? { risk } : {}),
      }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      resetAndClose()
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
    setWorkflowProfileId(null)
    setWorkflowTouched(false)
    setRisk(null)
    setRiskTouched(false)
    setSelectedTemplateId(null)
    onClose()
  }

  const profileOptions: WorkflowProfileInfo[] = useMemo(() => {
    const list = workflowProfiles ?? []
    const known = new Set(list.map((p) => p.id))
    const extras: WorkflowProfileInfo[] =
      workflowProfileId && !known.has(workflowProfileId)
        ? [{ id: workflowProfileId, displayName: workflowProfileId, description: '', isDefault: false }]
        : []
    return [...list, ...extras]
  }, [workflowProfiles, workflowProfileId])
  const { effectiveTemplateId: defaultProfileId } = useEffectiveDefaultWorkflowProfile()
  const workflowSelectValue = workflowProfileId ?? defaultProfileId ?? ''

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
              <p className="text-[11px] text-blue-600/80 mt-1">
                Pre-filled below. Change the selector to override.
              </p>
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
                    aria-pressed={risk === r}
                    onClick={() => {
                      setRisk(r)
                      setRiskTouched(true)
                    }}
                    className={`rounded-full capitalize ${
                      risk === r ? 'ring-1 ring-offset-1' : 'hover:opacity-80'
                    }`}
                    style={{
                      backgroundColor: style.bg,
                      color: style.text,
                      ...(risk === r ? { ringColor: style.text } : {}),
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

          {repositories && repositories.length > 1 && (
            <div>
              <label className="block text-xs font-medium text-foreground mb-1">Repository</label>
              <select
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
            </div>
          )}

          <div>
            <label className="block text-xs font-medium text-foreground mb-1">Coder Model</label>
            <ModelPresetSelect
              value={model}
              variant={modelVariant}
              onChange={setModel}
              onVariantChange={setModelVariant}
              onClear={() => setModel(null)}
            />
          </div>

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

          {mutation.error && (
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
