import { useState, useMemo, useCallback } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  BotIcon,
  Loader2Icon,
  ArchiveIcon,
} from 'lucide-react'
import {
  useCreateAgent,
  useUpdateAgent,
  useArchiveAgent,
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
}

export type AgentProfileEditorOperationsHook = () => AgentProfileEditorOperations

const useDefaultOperations: AgentProfileEditorOperationsHook = () => ({
  createAgent: useCreateAgent(),
  updateAgent: useUpdateAgent(),
  archiveAgent: useArchiveAgent(),
})

interface FormErrors {
  name?: string
  instructions?: string
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
  const { createAgent, updateAgent, archiveAgent } = operationsHook()
  const isEditing = !!agent
  const initialModelVariant = useMemo(() => readAgentModelAndVariant(agent), [agent])

  const [name, setName] = useState(agent?.name ?? '')
  const [instructions, setInstructions] = useState(agent?.instructions ?? '')
  const [skillsText, setSkillsText] = useState(agent?.skills?.join(', ') ?? '')
  const [model, setModel] = useState<string | null>(initialModelVariant.model)
  const [variant, setVariant] = useState<string | null>(initialModelVariant.variant)
  const [runtime, setRuntime] = useState<AgentRuntime>(initialModelVariant.runtime)
  const [errors, setErrors] = useState<FormErrors>({})
  const [archiveConfirmOpen, setArchiveConfirmOpen] = useState(false)

  const isSaving = createAgent.isPending || updateAgent.isPending

  const { data: availableModels } = useAvailableModelIds(runtime)
  const modelVariantsMap = useModelVariants(runtime)

  const allModels: string[] = useMemo(() => availableModels?.models ?? [], [availableModels])

  function validate(): FormErrors {
    const errs: FormErrors = {}
    if (!name.trim()) errs.name = 'Name is required'
    if (!instructions.trim()) errs.instructions = 'Instructions are required'
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

    if (isEditing && agent) {
      const payload: AgentUpdateRequest = {
        name: name.trim() || null,
        instructions: instructions.trim() || null,
        skills: skillsText.trim() ? skillsText.split(',').map((s) => s.trim()).filter(Boolean) : null,
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
        instructions: instructions.trim(),
        skills: skillsText.trim() ? skillsText.split(',').map((s) => s.trim()).filter(Boolean) : null,
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
        <DialogContent className="sm:max-w-lg" data-testid="agent-profile-editor">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <BotIcon className="size-4" />
              {isEditing ? 'Edit Agent' : 'New Agent'}
            </DialogTitle>
            <DialogDescription>
              {isEditing
                ? 'Changes to Instructions, Runtime, Model, Variant, and Skills apply only to Jobs created after saving. Executions already in progress and existing Sessions keep the configuration from launch.'
                : 'Create a new agent profile with instructions, model, and skills.'}
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
              <ModelSelect
                id="agent-model"
                value={model}
                placeholder="Select a model"
                models={allModels}
                onChange={(m) => setModel(m)}
                onChangeVariant={setVariant}
                onClear={() => { setModel(null); setVariant(null) }}
                allowClear={!!model}
                modelVariants={modelVariantsMap}
                valueVariant={variant}
                onChangeModelVariant={(m, v) => { setModel(m); setVariant(v) }}
              />
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
