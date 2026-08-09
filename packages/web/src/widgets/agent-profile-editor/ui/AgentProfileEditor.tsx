import { useState, useMemo, useCallback, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  BotIcon,
  Loader2Icon,
  ArchiveIcon,
  CheckIcon,
  ListIcon,
} from 'lucide-react'
import {
  useCreateAgent,
  useUpdateAgent,
  useArchiveAgent,
  useAgents,
  readAgentModelAndVariant,
  writeAgentModelAndVariant,
} from '../../../entities/agent'
import type { AgentInfo, AgentCreateRequest, AgentUpdateRequest } from '../../../entities/agent'
import { AGENT_RUNTIME_OPENCODE, AGENT_RUNTIME_PI, useAvailableModelIds, useModelVariants, type AgentRuntime } from '../../../entities/settings'
import { useProjectPath } from '../../../entities/project'
import { ModelSelect } from '../../../shared/ui/ModelSelect'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Textarea } from '@/shared/ui/components/textarea'
import { Label } from '@/shared/ui/components/label'
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription } from '@/shared/ui/components/dialog'
import { AGENT_TASK_FOCUSES, recommendModels, type AgentTaskFocus } from '../model/model-recommendations'

interface Props {
  agent?: AgentInfo | null
  open: boolean
  onClose: () => void
  onSaved?: (agent: AgentInfo) => void
  operationsHook?: AgentProfileEditorOperationsHook
}

export interface AgentProfileEditorOperations {
  createAgent: Pick<ReturnType<typeof useCreateAgent>, 'mutate' | 'isPending'>
  updateAgent: Pick<ReturnType<typeof useUpdateAgent>, 'mutate' | 'isPending'>
  archiveAgent: Pick<ReturnType<typeof useArchiveAgent>, 'mutate' | 'isPending'>
  availableAgents?: AgentInfo[]
}

export type AgentProfileEditorOperationsHook = () => AgentProfileEditorOperations

const useDefaultOperations: AgentProfileEditorOperationsHook = () => ({
  createAgent: useCreateAgent(),
  updateAgent: useUpdateAgent(),
  archiveAgent: useArchiveAgent(),
  availableAgents: useAgents().data,
})

interface FormErrors {
  name?: string
  instructions?: string
  maxConcurrentRuns?: string
  api?: string
}

export function AgentProfileEditor({
  agent,
  open,
  onClose,
  onSaved,
  operationsHook = useDefaultOperations,
}: Props) {
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { createAgent, updateAgent, archiveAgent, availableAgents = [] } = operationsHook()
  const isEditing = !!agent
  const initialModelVariant = useMemo(() => readAgentModelAndVariant(agent), [agent])

  const [name, setName] = useState(agent?.name ?? '')
  const [description, setDescription] = useState(agent?.description ?? '')
  const [avatar, setAvatar] = useState(agent?.avatar ?? '')
  const [instructions, setInstructions] = useState(agent?.instructions ?? '')
  const [skillsText, setSkillsText] = useState(agent?.skills?.join(', ') ?? '')
  const [subagentsText, setSubagentsText] = useState(agent?.allowedSubagentAgentIds?.join(', ') ?? '')
  const [maxConcurrentRuns, setMaxConcurrentRuns] = useState(agent?.maxConcurrentRuns == null ? '' : String(agent.maxConcurrentRuns))
  const [model, setModel] = useState<string | null>(initialModelVariant.model)
  const [variant, setVariant] = useState<string | null>(initialModelVariant.variant)
  const [runtime, setRuntime] = useState<AgentRuntime>(initialModelVariant.runtime)
  const [taskFocus, setTaskFocus] = useState<AgentTaskFocus>('general')
  const [showFullCatalog, setShowFullCatalog] = useState(false)
  const [errors, setErrors] = useState<FormErrors>({})
  const [archiveConfirmOpen, setArchiveConfirmOpen] = useState(false)

  const isSaving = createAgent.isPending || updateAgent.isPending

  const { data: availableModels } = useAvailableModelIds(runtime)
  const modelVariantsMap = useModelVariants(runtime)

  const allModels: string[] = useMemo(() => availableModels?.models ?? [], [availableModels])
  const recommendedModels = useMemo(
    () => recommendModels(allModels, taskFocus, `${description} ${instructions}`),
    [allModels, description, instructions, taskFocus],
  )
  const visibleModels = showFullCatalog ? allModels : recommendedModels.slice(0, 8)
  const availableSubagents = useMemo(
    () => availableAgents.filter((candidate) => candidate.id !== agent?.id && candidate.status !== 'archived'),
    [agent?.id, availableAgents],
  )

  useEffect(() => {
    if (!open) return
    setName(agent?.name ?? '')
    setDescription(agent?.description ?? '')
    setAvatar(agent?.avatar ?? '')
    setInstructions(agent?.instructions ?? '')
    setSkillsText(agent?.skills?.join(', ') ?? '')
    setSubagentsText(agent?.allowedSubagentAgentIds?.join(', ') ?? '')
    setMaxConcurrentRuns(agent?.maxConcurrentRuns == null ? '' : String(agent.maxConcurrentRuns))
    setModel(initialModelVariant.model)
    setVariant(initialModelVariant.variant)
    setRuntime(initialModelVariant.runtime)
    setTaskFocus('general')
    setShowFullCatalog(false)
    setErrors({})
  }, [agent, initialModelVariant, open])

  function validate(): FormErrors {
    const errs: FormErrors = {}
    if (!name.trim()) errs.name = 'Name is required'
    if (!instructions.trim()) errs.instructions = 'Instructions are required'
    if (maxConcurrentRuns.trim() && (!Number.isInteger(Number(maxConcurrentRuns)) || Number(maxConcurrentRuns) < 1)) {
      errs.maxConcurrentRuns = 'Use a positive whole number or leave this blank'
    }
    return errs
  }

  async function handleSave() {
    const validation = validate()
    setErrors(validation)
    if (Object.keys(validation).length > 0) return

    const agentConfig = writeAgentModelAndVariant(
      agent?.agentConfig ?? null,
      model,
      variant,
      runtime,
    )
    const parsedMaxConcurrentRuns = maxConcurrentRuns.trim() ? Number(maxConcurrentRuns) : null
    const allowedSubagentAgentIds = subagentsText.trim()
      ? subagentsText.split(',').map((value) => value.trim()).filter(Boolean)
      : null

    if (isEditing && agent) {
      const payload: AgentUpdateRequest = {
        name: name.trim() || null,
        avatar: avatar.trim() || null,
        description: description.trim() || null,
        instructions: instructions.trim() || null,
        skills: skillsText.trim() ? skillsText.split(',').map((s) => s.trim()).filter(Boolean) : null,
        allowedSubagentAgentIds,
        maxConcurrentRuns: parsedMaxConcurrentRuns,
        agentConfig,
      }
      updateAgent.mutate(
        { agentRef: agent.id, data: payload },
        {
          onSuccess: (updated) => {
            onSaved?.(updated)
            onClose()
          },
          onError: (err) => {
            setErrors({ api: err.message })
          },
        },
      )
    } else {
      const payload: AgentCreateRequest = {
        name: name.trim(),
        avatar: avatar.trim() || null,
        description: description.trim() || null,
        instructions: instructions.trim(),
        skills: skillsText.trim() ? skillsText.split(',').map((s) => s.trim()).filter(Boolean) : null,
        allowedSubagentAgentIds,
        maxConcurrentRuns: parsedMaxConcurrentRuns,
        agentConfig,
      }
      createAgent.mutate(payload, {
        onSuccess: (created) => {
          onSaved?.(created)
          onClose()
          navigate(toProjectPath(`/agents/${encodeURIComponent(created.id)}`))
        },
        onError: (err) => {
          setErrors({ api: err.message })
        },
      })
    }
  }

  function handleArchive() {
    if (!agent) return
    archiveAgent.mutate(agent.id, {
      onSuccess: () => {
        setArchiveConfirmOpen(false)
        onClose()
      },
      onError: (err) => {
        setErrors({ api: err.message })
      },
    })
  }

  const handleClose = useCallback(() => {
    if (!isSaving) onClose()
  }, [isSaving, onClose])

  return (
    <>
      <Dialog open={open} onOpenChange={(open) => { if (!open) handleClose() }}>
        <DialogContent className="sm:max-w-2xl" data-testid="agent-profile-editor">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <BotIcon className="size-4" />
              {isEditing ? 'Edit Agent' : 'New Agent'}
            </DialogTitle>
            <DialogDescription>
              {isEditing
                ? 'Saved changes are used by Jobs created after saving. Executions already in progress and existing Sessions keep the identity and configuration captured at launch.'
                : 'Create a reusable Agent identity around a task, with a Runtime and execution scope for future Jobs.'}
            </DialogDescription>
          </DialogHeader>

          <div className="space-y-4 py-2">
            {errors.api && (
              <div
                data-testid="editor-api-error"
                className="rounded-md bg-red-50 border border-red-200 px-3 py-2 text-xs text-red-600"
              >
                {errors.api}
              </div>
            )}

            <div className="space-y-1.5">
              <Label htmlFor="agent-runtime">Execution backend</Label>
              <select
                id="agent-runtime"
                aria-label="Execution backend"
                data-testid="agent-runtime"
                value={runtime}
                onChange={(event) => {
                  setRuntime(event.target.value as AgentRuntime)
                  setModel(null)
                  setVariant(null)
                }}
                className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm"
              >
                <option value={AGENT_RUNTIME_OPENCODE}>OpenCode</option>
                <option value={AGENT_RUNTIME_PI}>Pi</option>
              </select>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="agent-name">Name *</Label>
              <Input
                id="agent-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="My Agent"
                data-testid="editor-name"
                className={errors.name ? 'border-red-500' : ''}
              />
              {errors.name && (
                <p data-testid="editor-name-error" className="text-xs text-red-500">{errors.name}</p>
              )}
            </div>

            <div className="grid gap-4 sm:grid-cols-[1fr_8rem]">
              <div className="space-y-1.5">
                <Label htmlFor="agent-purpose">Purpose / description</Label>
                <Textarea
                  id="agent-purpose"
                  value={description}
                  onChange={(e) => setDescription(e.target.value)}
                  placeholder="What task is this Agent for?"
                  rows={2}
                  data-testid="editor-description"
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="agent-avatar">Avatar</Label>
                <Input
                  id="agent-avatar"
                  value={avatar}
                  onChange={(e) => setAvatar(e.target.value)}
                  placeholder="Icon or emoji"
                  data-testid="editor-avatar"
                />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="agent-instructions">Instructions *</Label>
              <Textarea
                id="agent-instructions"
                value={instructions}
                onChange={(e) => setInstructions(e.target.value)}
                placeholder="You are a helpful assistant that..."
                rows={4}
                data-testid="editor-instructions"
                className={errors.instructions ? 'border-red-500' : ''}
              />
              {errors.instructions && (
                <p data-testid="editor-instructions-error" className="text-xs text-red-500">{errors.instructions}</p>
              )}
            </div>

            <div className="space-y-1.5">
              <Label>Model</Label>
              <div className="grid gap-2 sm:grid-cols-[minmax(0,12rem)_1fr]">
                <select
                  aria-label="Task use"
                  data-testid="editor-task-focus"
                  value={taskFocus}
                  onChange={(event) => {
                    setTaskFocus(event.target.value as AgentTaskFocus)
                    setShowFullCatalog(false)
                  }}
                  className="h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm"
                >
                  {AGENT_TASK_FOCUSES.map((focus) => (
                    <option key={focus.value} value={focus.value}>{focus.label}</option>
                  ))}
                </select>
                <p className="flex items-center text-xs text-muted-foreground">
                  Recommendations come from the selected Runtime catalog.
                </p>
              </div>
              <ModelSelect
                id="agent-model"
                value={model}
                placeholder="Select a model"
                models={visibleModels}
                onChange={(m) => setModel(m)}
                onChangeVariant={setVariant}
                onClear={() => { setModel(null); setVariant(null) }}
                allowClear={!!model}
                modelVariants={modelVariantsMap}
                valueVariant={variant}
                onChangeModelVariant={(m, v) => { setModel(m); setVariant(v) }}
              />
              <div className="flex items-center justify-between gap-2">
                <span data-testid="model-directory-summary" className="text-[10px] text-muted-foreground">
                  {showFullCatalog ? `Complete catalog · ${allModels.length} models` : `Recommended · ${visibleModels.length} of ${allModels.length}`}
                </span>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  onClick={() => setShowFullCatalog((current) => !current)}
                  data-testid="model-directory-toggle"
                  className="h-7 px-2 text-xs"
                >
                  <ListIcon />
                  {showFullCatalog ? 'Show recommendations' : 'Browse full catalog'}
                </Button>
              </div>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="agent-skills">Skills</Label>
              <Input
                id="agent-skills"
                value={skillsText}
                onChange={(e) => setSkillsText(e.target.value)}
                placeholder="skill1, skill2, skill3"
                data-testid="editor-skills"
              />
              <p className="text-[10px] text-muted-foreground">Comma-separated list of skills.</p>
            </div>

            <div className="space-y-1.5">
              <Label htmlFor="agent-subagents">Allowed subagents</Label>
              <Input
                id="agent-subagents"
                value={subagentsText}
                onChange={(e) => setSubagentsText(e.target.value)}
                placeholder="Agent IDs, comma-separated"
                data-testid="editor-subagents"
              />
              {availableSubagents.length > 0 && (
                <div className="flex flex-wrap gap-2 pt-1" data-testid="editor-subagent-options">
                  {availableSubagents.map((candidate) => {
                    const selected = subagentsText.split(',').map((value) => value.trim()).includes(candidate.id)
                    return (
                      <button
                        type="button"
                        key={candidate.id}
                        aria-pressed={selected}
                        onClick={() => {
                          const selectedIds = new Set(subagentsText.split(',').map((value) => value.trim()).filter(Boolean))
                          if (selected) selectedIds.delete(candidate.id)
                          else selectedIds.add(candidate.id)
                          setSubagentsText(Array.from(selectedIds).join(', '))
                        }}
                        className={`inline-flex items-center gap-1 rounded-md border px-2 py-1 text-xs ${selected ? 'border-blue-300 bg-blue-50 text-blue-700' : 'border-border text-muted-foreground'}`}
                      >
                        {selected && <CheckIcon className="size-3" />}
                        {candidate.name}
                      </button>
                    )
                  })}
                </div>
              )}
            </div>

            <div className="grid gap-4 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label htmlFor="agent-max-concurrent-runs">Concurrency intent</Label>
                <Input
                  id="agent-max-concurrent-runs"
                  type="number"
                  min={1}
                  step={1}
                  value={maxConcurrentRuns}
                  onChange={(e) => setMaxConcurrentRuns(e.target.value)}
                  placeholder="No limit"
                  data-testid="editor-concurrency"
                  className={errors.maxConcurrentRuns ? 'border-red-500' : ''}
                />
                {errors.maxConcurrentRuns && <p data-testid="editor-concurrency-error" className="text-xs text-red-500">{errors.maxConcurrentRuns}</p>}
                <p className="text-[10px] text-muted-foreground">Saved for future scheduling decisions; this form does not claim a Server capacity change.</p>
              </div>
              <div className="space-y-1.5 rounded-md border border-border bg-muted/20 p-3" data-testid="editor-permissions">
                <p className="text-sm font-medium">Permissions</p>
                <p className="text-xs text-muted-foreground">Runtime-managed permission prompts apply when this Agent runs. The launch review will show the repository, workspace, Issue, and Epic scope selected for each Job.</p>
              </div>
            </div>
          </div>

          <div className="flex items-center justify-between gap-2 pt-2 border-t">
            <div>
              {isEditing && agent?.status !== 'archived' && (
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setArchiveConfirmOpen(true)}
                  className="text-red-600 hover:text-red-700 hover:bg-red-50"
                  data-testid="editor-archive"
                  disabled={archiveAgent.isPending}
                >
                  <ArchiveIcon />
                  Archive
                </Button>
              )}
            </div>
            <div className="flex items-center gap-2">
              <Button variant="outline" onClick={handleClose} disabled={isSaving}>
                Cancel
              </Button>
              <Button onClick={handleSave} disabled={isSaving} data-testid="editor-save">
                {isSaving && <Loader2Icon className="size-4 animate-spin" />}
                {isEditing ? 'Save Changes' : 'Create Agent'}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <Dialog open={archiveConfirmOpen} onOpenChange={setArchiveConfirmOpen}>
        <DialogContent className="sm:max-w-sm">
          <DialogHeader>
            <DialogTitle>Archive Agent</DialogTitle>
            <DialogDescription>
              This agent will be marked as archived. It will leave the Active
              group and cannot be used to start new sessions. You can restore
              it from the agent detail page.
            </DialogDescription>
          </DialogHeader>
          <div className="flex justify-end gap-2 pt-2">
            <Button variant="outline" onClick={() => setArchiveConfirmOpen(false)} disabled={archiveAgent.isPending}>
              Cancel
            </Button>
            <Button
              variant="destructive"
              onClick={handleArchive}
              disabled={archiveAgent.isPending}
              data-testid="editor-archive-confirm"
            >
              {archiveAgent.isPending && <Loader2Icon className="size-4 animate-spin" />}
              Archive
            </Button>
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
