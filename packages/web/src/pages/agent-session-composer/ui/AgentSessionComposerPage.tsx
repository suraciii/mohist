import { useCallback, useEffect, useMemo, useRef, useState, type ComponentProps, type ComponentType } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { BotIcon, ChevronDownIcon, XIcon, AlertTriangleIcon, SearchIcon, InfoIcon } from 'lucide-react'
import {
  getAgentAvailabilityFeedback,
  getAgentLaunchErrorFeedback,
  useAgentListAvailability,
  useAgents,
  useLaunchAgentSession,
  usePreflightAgentTask,
  useStartAgentTask,
} from '../../../entities/agent'
import type {
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentReadinessResult,
  AgentSessionLaunchContext,
  AgentSessionLaunchResponse,
  AgentTaskLaunchInput,
  AgentTaskPreflightResponse,
} from '../../../entities/agent'
import { extractAttachmentIds } from '../../../entities/issue'
import { useProject, useProjectPath } from '../../../entities/project'
import {
  AGENT_RUNTIME_OPENCODE,
  AGENT_RUNTIME_PI,
  useAvailableModelIds,
  useModelVariants,
  type AgentRuntime,
} from '../../../entities/settings'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'
import { AttachmentComposer as DefaultAttachmentComposer } from '../../../shared/ui/attachment-composer'
import {
  AttachmentResults,
  type AttachmentResultAccepted,
  type AttachmentResultRejected,
} from '../../../shared/ui/attachment-results'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { Badge } from '@/shared/ui/components/badge'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { cn } from '@/shared/lib/utils'
import { ModelSelect } from '../../../shared/ui/ModelSelect'

interface ContextRef {
  type: 'issue' | 'epic' | 'repository' | 'workspace'
  label: string
  value: string
}

function ContextRefChip({ refItem, onRemove }: { refItem: ContextRef; onRemove: () => void }) {
  return (
    <span
      data-testid={`context-ref-chip-${refItem.type}`}
      className="inline-flex items-center gap-1 rounded-full bg-muted px-2.5 py-1 text-xs font-medium text-foreground"
    >
      {refItem.label}
      <button
        type="button"
        onClick={onRemove}
        aria-label={`Remove ${refItem.label}`}
        data-testid={`remove-ref-${refItem.type}`}
        className="ml-0.5 inline-flex size-4 items-center justify-center rounded-full hover:bg-muted-foreground/20"
      >
        <XIcon className="size-3" />
      </button>
    </span>
  )
}

function AgentSelector({
  agents,
  selectedRef,
  onChange,
  isLoading,
}: {
  agents: AgentInfo[] | undefined
  selectedRef: string
  onChange: (ref: string) => void
  isLoading: boolean
}) {
  const [open, setOpen] = useState(false)
  const [search, setSearch] = useState('')
  const selectedAgent = agents?.find((a) => a.id === selectedRef) ?? null

  const filtered = useMemo(() => {
    if (!agents) return []
    if (!search.trim()) return agents
    const q = search.toLowerCase()
    return agents.filter((a) => a.name.toLowerCase().includes(q) || a.id.toLowerCase().includes(q))
  }, [agents, search])

  if (isLoading) {
    return (
      <Button variant="outline" className="w-full justify-between" disabled>
        <span className="text-muted-foreground">Loading agents...</span>
        <ChevronDownIcon className="size-4 text-muted-foreground" />
      </Button>
    )
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger
        render={
          <Button variant="outline" data-testid="agent-selector-trigger" className="w-full justify-between">
            {selectedAgent ? (
              <span className="truncate">{selectedAgent.name}</span>
            ) : (
              <span className="text-muted-foreground">New Agent for this task</span>
            )}
            <ChevronDownIcon className="size-4 shrink-0 text-muted-foreground" />
          </Button>
        }
      />
      <PopoverContent className="w-80 p-0" align="start">
        <div className="p-2">
          <div className="relative">
            <SearchIcon className="absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Search agents..."
              className="pl-8 h-8 text-sm"
              data-testid="agent-search-input"
            />
          </div>
        </div>
        <div className="max-h-64 overflow-y-auto border-t">
          <div
            role="button"
            tabIndex={0}
            data-testid="agent-option-new-task"
            onClick={() => {
              onChange('')
              setOpen(false)
              setSearch('')
            }}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                onChange('')
                setOpen(false)
                setSearch('')
              }
            }}
            className={cn(
              'flex items-center gap-2 px-3 py-2 cursor-pointer text-sm border-b',
              selectedRef === '' ? 'bg-muted' : 'hover:bg-muted',
            )}
          >
            <BotIcon className="size-4 shrink-0 text-muted-foreground" />
            <span className="font-medium text-foreground">New Agent for this task</span>
          </div>
          {filtered.length === 0 && (
            <div className="px-3 py-4 text-center text-sm text-muted-foreground">No agents found</div>
          )}
          {filtered.map((agent) => {
            const isSelected = agent.id === selectedRef
            const isArchived = agent.status === 'archived'
            return (
              <div
                key={agent.id}
                role="button"
                tabIndex={0}
                data-testid={`agent-option-${agent.id}`}
                data-agent-ref={agent.id}
                data-archived={isArchived ? 'true' : 'false'}
                onClick={() => {
                  onChange(agent.id)
                  setOpen(false)
                  setSearch('')
                }}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') {
                    onChange(agent.id)
                    setOpen(false)
                    setSearch('')
                  }
                }}
                className={cn(
                  'flex items-center gap-2 px-3 py-2 cursor-pointer text-sm',
                  isSelected ? 'bg-muted' : 'hover:bg-muted',
                )}
              >
                <BotIcon className={cn('size-4 shrink-0', isArchived ? 'text-muted-foreground' : 'text-blue-600')} />
                <span className="flex-1 truncate font-medium">{agent.name}</span>
                {isArchived && (
                  <Badge variant="outline" className="text-[10px] px-1 py-0 h-4 text-muted-foreground">
                    Archived
                  </Badge>
                )}
              </div>
            )
          })}
        </div>
      </PopoverContent>
    </Popover>
  )
}

function TaskExecutionConfigControls({
  runtime,
  model,
  variant,
  onRuntimeChange,
  onModelChange,
  onVariantChange,
}: {
  runtime: AgentRuntime
  model: string | null
  variant: string | null
  onRuntimeChange: (runtime: AgentRuntime) => void
  onModelChange: (model: string | null) => void
  onVariantChange: (variant: string | null) => void
}) {
  const { data: availableModels } = useAvailableModelIds(runtime)
  const modelVariants = useModelVariants(runtime)
  const models = availableModels?.models ?? []

  return (
    <div data-testid="execution-config-controls" className="space-y-3 rounded-lg border border-border bg-card p-4">
      <div>
        <p className="text-sm font-medium text-foreground">Execution configuration</p>
        <p className="text-xs text-muted-foreground mt-0.5">
          Choose the Runtime and a catalog model for this task. Variant is optional.
        </p>
      </div>
      <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor="task-runtime">Runtime</Label>
          <select
            id="task-runtime"
            data-testid="task-runtime"
            aria-label="Runtime"
            value={runtime}
            onChange={(event) => onRuntimeChange(event.target.value as AgentRuntime)}
            className="w-full h-9 rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm"
          >
            <option value={AGENT_RUNTIME_OPENCODE}>OpenCode</option>
            <option value={AGENT_RUNTIME_PI}>Pi</option>
          </select>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="task-model">Model</Label>
          <ModelSelect
            id="task-model"
            value={model}
            placeholder={availableModels ? 'Select a catalog model' : 'Loading catalog models...'}
            models={models}
            onChange={(nextModel) => onModelChange(nextModel)}
            onChangeVariant={onVariantChange}
            modelVariants={modelVariants}
            valueVariant={variant}
            onChangeModelVariant={(nextModel, nextVariant) => {
              onModelChange(nextModel)
              onVariantChange(nextVariant)
            }}
            disabled={!availableModels}
          />
        </div>
      </div>
      <p data-testid="execution-config-catalog-hint" className="text-[11px] text-muted-foreground">
        Models and variants come from the selected Runtime catalog.
      </p>
    </div>
  )
}

export interface AgentSessionComposerPageComponents {
  AttachmentComposer: ComponentType<ComponentProps<typeof DefaultAttachmentComposer>>
}

export interface AgentSessionComposerData {
  agents: AgentInfo[] | undefined
  agentsLoading: boolean
  availability: AgentAvailabilitySummaryEntry[] | undefined
  availabilityLoading: boolean
  launchMutation: Pick<ReturnType<typeof useLaunchAgentSession>, 'mutate' | 'isPending' | 'error' | 'reset'>
  preflightTaskMutation?: Pick<ReturnType<typeof usePreflightAgentTask>, 'mutate' | 'isPending' | 'error' | 'reset'>
  startTaskMutation: Pick<ReturnType<typeof useStartAgentTask>, 'mutate' | 'isPending' | 'error' | 'reset'>
}

export type AgentSessionComposerDataHook = () => AgentSessionComposerData

const useDefaultData: AgentSessionComposerDataHook = () => {
  const { data: agents, isLoading: agentsLoading } = useAgents()
  const { data: availability, isLoading: availabilityLoading } = useAgentListAvailability()
  return {
    agents,
    agentsLoading,
    availability,
    availabilityLoading,
    launchMutation: useLaunchAgentSession(),
    preflightTaskMutation: usePreflightAgentTask(),
    startTaskMutation: useStartAgentTask(),
  }
}

const defaultComponents: AgentSessionComposerPageComponents = {
  AttachmentComposer: DefaultAttachmentComposer,
}

export function AgentSessionComposerPage({
  components,
  dataHook = useDefaultData,
}: {
  components?: Partial<AgentSessionComposerPageComponents>
  dataHook?: AgentSessionComposerDataHook
} = {}) {
  const { AttachmentComposer } = { ...defaultComponents, ...components }
  useDocumentTitle('New Session — Mohist')
  const navigate = useNavigate()
  const toProjectPath = useProjectPath()
  const { projectId, currentProject } = useProject()
  const [searchParams] = useSearchParams()

  const {
    agents,
    agentsLoading,
    availability,
    availabilityLoading,
    launchMutation,
    preflightTaskMutation,
    startTaskMutation,
  } = dataHook()

  const launchableAgents = useMemo(() => agents?.filter((a) => a.status !== 'archived') ?? [], [agents])

  const [selectedAgentRef, setSelectedAgentRef] = useState(() => searchParams.get('agent') || '')
  const [contextRefs, setContextRefs] = useState<ContextRef[]>(() => {
    const refs: ContextRef[] = []
    const issue = searchParams.get('issue')
    if (issue) refs.push({ type: 'issue', label: `Issue #${issue}`, value: issue })
    const epic = searchParams.get('epic')
    if (epic) refs.push({ type: 'epic', label: `Epic: ${epic}`, value: epic })
    const repo = searchParams.get('repo')
    if (repo) refs.push({ type: 'repository', label: `Repository: ${repo}`, value: repo })
    const ws = searchParams.get('ws')
    if (ws) refs.push({ type: 'workspace', label: `Workspace: ${ws}`, value: ws })
    return refs
  })

  const [prompt, setPrompt] = useState('')
  const [promptTouched, setPromptTouched] = useState(false)
  const [executionRuntime, setExecutionRuntime] = useState<AgentRuntime>(AGENT_RUNTIME_OPENCODE)
  const [executionModel, setExecutionModel] = useState<string | null>(null)
  const [executionVariant, setExecutionVariant] = useState<string | null>(null)
  const [executionConfigAdjusted, setExecutionConfigAdjusted] = useState(false)
  const [allowedCollaboratorIds, setAllowedCollaboratorIds] = useState<string[]>([])
  const [maxConcurrentRunsText, setMaxConcurrentRunsText] = useState('')
  const [pendingPreflight, setPendingPreflight] = useState<{
    response: AgentTaskPreflightResponse
    input: AgentTaskLaunchInput
  } | null>(null)
  const [launchAttachmentResult, setLaunchAttachmentResult] = useState<{
    agentId: string
    agentName: string
    accepted: AttachmentResultAccepted[]
    rejected: AttachmentResultRejected[]
    sessionPath: string
  } | null>(null)
  const launchKeyRef = useRef<string | null>(null)
  const defaultExecutionConfig = currentProject?.defaultExecutionConfig ?? null

  useEffect(() => {
    if (executionConfigAdjusted || !defaultExecutionConfig) return
    setExecutionRuntime(defaultExecutionConfig.runtime)
    setExecutionModel(defaultExecutionConfig.model)
    setExecutionVariant(defaultExecutionConfig.variant ?? null)
  }, [defaultExecutionConfig, executionConfigAdjusted])

  const selectedAgent = useMemo(
    () => agents?.find((a) => a.id === selectedAgentRef) ?? null,
    [agents, selectedAgentRef],
  )
  const selectedAvailability = useMemo(
    () => availability?.find((entry) => entry.agentId === selectedAgentRef),
    [availability, selectedAgentRef],
  )
  const isArchived = selectedAgent?.status === 'archived'
  const selectedReadiness: AgentReadinessResult | null | undefined = selectedAgent?.readiness
  const readinessConclusion = selectedReadiness?.conclusion ?? 'Unknown'
  const isNeedsSetup = readinessConclusion === 'Needs setup'
  const isUnknownReadiness = readinessConclusion === 'Unknown'
  const launchBlockedByReadiness = isNeedsSetup

  const promptEmpty = !prompt.trim()
  const attachmentIds = useMemo(() => extractAttachmentIds(prompt), [prompt])
  const showPromptError = promptTouched && promptEmpty && attachmentIds.length === 0

  const isCreatingAgent = !selectedAgentRef
  const executionConfigResolvable = !!defaultExecutionConfig || !!executionModel
  const executionControlsVisible = isCreatingAgent && (!defaultExecutionConfig || executionConfigAdjusted)
  const concurrencyValue = maxConcurrentRunsText.trim() ? Number(maxConcurrentRunsText) : null
  const concurrencyValid = concurrencyValue === null || (Number.isInteger(concurrencyValue) && concurrencyValue > 0)
  const launchPending =
    launchMutation.isPending || preflightTaskMutation?.isPending === true || startTaskMutation.isPending
  const canLaunch =
    (!promptEmpty || attachmentIds.length > 0) &&
    (!isCreatingAgent || executionConfigResolvable) &&
    concurrencyValid &&
    (!selectedAgentRef || (!isArchived && !launchBlockedByReadiness)) &&
    !launchPending

  const removeRef = useCallback((index: number) => {
    setContextRefs((prev) => prev.filter((_, i) => i !== index))
  }, [])

  const handleLaunchSuccess = useCallback(
    (data: AgentSessionLaunchResponse) => {
      const fallbackJobQuery = data.jobId ? `?jobId=${encodeURIComponent(data.jobId)}` : ''
      const sessionPath =
        data.sessionUrl ?? `${toProjectPath(`/sessions/${encodeURIComponent(data.sessionId)}`)}${fallbackJobQuery}`
      const accepted = data.attachments ?? []
      const rejected = data.rejectedAttachments ?? []
      launchKeyRef.current = null
      if (accepted.length > 0 || rejected.length > 0) {
        setLaunchAttachmentResult({
          agentId: data.agentId,
          agentName: data.agentName,
          accepted,
          rejected,
          sessionPath,
        })
        return
      }
      navigate(sessionPath)
    },
    [navigate, toProjectPath],
  )

  const handleConfirmPreflight = useCallback(() => {
    if (!pendingPreflight || !launchKeyRef.current) return
    const { response, input } = pendingPreflight
    setPendingPreflight(null)
    startTaskMutation.mutate(
      {
        ...input,
        preflightFingerprint: response.scopeFingerprint,
        idempotencyKey: launchKeyRef.current,
      },
      { onSuccess: handleLaunchSuccess },
    )
  }, [handleLaunchSuccess, pendingPreflight, startTaskMutation])

  const handleLaunch = useCallback(() => {
    if (!canLaunch) return

    const context: AgentSessionLaunchContext = {}
    for (const ref of contextRefs) {
      const number = Number(ref.value)
      if ((ref.type === 'issue' || ref.type === 'epic') && Number.isInteger(number) && number > 0) {
        if (ref.type === 'issue') context.issueNumber = number
        else context.epicNumber = number
      } else if (ref.type === 'repository') context.repository = ref.value
      else if (ref.type === 'workspace') {
        if (selectedAgentRef) context.workspacePath = ref.value
        else context.workspace = ref.value
      }
    }
    const hasContext = Object.keys(context).length > 0
    const idempotencyKey = (launchKeyRef.current ??= createIdempotencyKey())
    const onSuccess = handleLaunchSuccess

    if (selectedAgentRef) {
      launchMutation.mutate(
        {
          agentRef: selectedAgentRef,
          prompt: prompt.trim(),
          context: hasContext ? context : null,
          attachments: attachmentIds,
          idempotencyKey,
        },
        { onSuccess },
      )
      return
    }

    const taskInput: AgentTaskLaunchInput = {
      prompt: prompt.trim(),
      context: hasContext ? context : null,
      attachments: attachmentIds,
    }
    if (allowedCollaboratorIds.length > 0) taskInput.allowedSubagentAgentIds = allowedCollaboratorIds
    if (maxConcurrentRunsText.trim()) taskInput.maxConcurrentRuns = Number(maxConcurrentRunsText)
    if (!defaultExecutionConfig || executionConfigAdjusted) {
      taskInput.runtime = executionRuntime
      taskInput.model = executionModel
      taskInput.variant = executionVariant
    }
    if (!preflightTaskMutation) {
      startTaskMutation.mutate({ ...taskInput, idempotencyKey }, { onSuccess })
      return
    }

    preflightTaskMutation.mutate(
      { ...taskInput, idempotencyKey },
      { onSuccess: (response) => setPendingPreflight({ response, input: taskInput }) },
    )
  }, [
    attachmentIds,
    canLaunch,
    contextRefs,
    defaultExecutionConfig,
    executionConfigAdjusted,
    executionModel,
    executionRuntime,
    executionVariant,
    allowedCollaboratorIds,
    maxConcurrentRunsText,
    launchMutation,
    handleLaunchSuccess,
    preflightTaskMutation,
    prompt,
    selectedAgentRef,
    startTaskMutation,
    toProjectPath,
  ])

  const launchError = selectedAgentRef
    ? launchMutation.error
    : (preflightTaskMutation?.error ?? startTaskMutation.error)
  const launchFeedback = getAgentLaunchErrorFeedback(launchError, selectedReadiness)
  const isNeedsSetupError = launchFeedback?.kind === 'needs-setup'
  const launchErrorData =
    launchError && 'data' in launchError
      ? (
          launchError as {
            data?: {
              gaps?: Array<{ code?: string; message?: string; action?: string }>
              setup?: { label?: string; path?: string } | null
            }
          }
        ).data
      : undefined
  const gapsFromError = isNeedsSetupError
    ? ((launchError && 'data' in launchError ? launchErrorData?.gaps : undefined) ?? selectedReadiness?.gaps)
    : undefined
  const setupFromError =
    isNeedsSetupError && launchErrorData?.setup?.label && launchErrorData.setup.path
      ? launchErrorData.setup
      : selectedReadiness?.setup

  const availabilityFeedback =
    selectedAvailability && !selectedAvailability.canStartNow
      ? getAgentAvailabilityFeedback(selectedAvailability.waitingReason)
      : undefined
  const availabilityFeedbackLoading = !selectedAvailability && availabilityLoading
  const launchErrorTestId =
    launchFeedback?.kind === 'runner-offline'
      ? 'error-no-runner'
      : launchFeedback?.kind === 'execution-unavailable' &&
          launchError &&
          'code' in launchError &&
          (launchError as { code?: string }).code === 'EXTERNAL_AGENT_UNAVAILABLE'
        ? 'error-external-agent'
        : launchFeedback?.kind === 'needs-setup'
          ? 'error-needs-setup'
          : launchFeedback?.kind === 'back-pressure'
            ? 'error-back-pressure'
            : launchFeedback?.kind === 'launch-conflict'
              ? 'error-launch-conflict'
              : launchFeedback?.kind === 'launch-pending'
                ? 'error-launch-pending'
                : launchFeedback?.kind === 'execution-config-unresolvable'
                  ? 'error-execution-config'
                  : 'error-execution-unavailable'

  return (
    <div data-testid="agent-session-composer-page" className="flex-1 overflow-y-auto bg-background">
      <div className="mx-auto max-w-4xl px-6 py-6 space-y-6">
        <div>
          <h1 className="text-lg font-semibold text-foreground">New Session</h1>
          <p className="text-xs text-muted-foreground mt-0.5">
            Start a new agent session with a prompt and optional context references.
          </p>
        </div>

        {launchFeedback && (
          <div
            data-testid={launchErrorTestId}
            data-feedback-kind={launchFeedback.kind}
            className={`flex flex-col gap-1 rounded-lg border px-3 py-2.5 text-sm ${launchFeedback.kind === 'needs-setup' || launchFeedback.kind === 'execution-unavailable' || launchFeedback.kind === 'execution-config-unresolvable' ? 'border-red-200 bg-red-50 text-red-800' : 'border-amber-200 bg-amber-50 text-amber-800'}`}
          >
            <div className="flex items-start gap-2">
              <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" />
              <span className="font-medium">{launchFeedback.title}</span>
            </div>
            <p className="ml-6 text-xs">
              {launchFeedback.message} {launchFeedback.nextAction}
            </p>
            {launchFeedback.kind === 'launch-conflict' && (
              <Button
                type="button"
                variant="outline"
                size="sm"
                data-testid="reset-launch-key"
                className="ml-6 w-fit"
                onClick={() => {
                  launchKeyRef.current = null
                  if (selectedAgentRef) launchMutation.reset()
                  else {
                    preflightTaskMutation?.reset()
                    startTaskMutation.reset()
                  }
                }}
              >
                Start with a new launch key
              </Button>
            )}
            {isNeedsSetupError && gapsFromError && gapsFromError.length > 0 && (
              <ul className="ml-6 list-disc space-y-0.5">
                {gapsFromError.map((gap) => (
                  <li key={`${gap.code ?? gap.message}-${gap.action}`} className="text-xs">
                    <span className="font-medium">{gap.message}</span>
                    {gap.action && <span> — {gap.action}</span>}
                  </li>
                ))}
              </ul>
            )}
            {isNeedsSetupError && setupFromError && (
              <p className="ml-6 text-xs">
                Fix in{' '}
                <a className="font-semibold underline" href={toProjectPath(setupFromError.path)}>
                  {setupFromError.label}
                </a>
                .
              </p>
            )}
          </div>
        )}

        {selectedAgent && !launchError && (selectedAvailability || availabilityFeedbackLoading) && (
          <div
            data-testid="agent-availability-feedback"
            data-feedback-kind={availabilityFeedback?.kind ?? 'unknown'}
            className={`flex items-start gap-2 rounded-lg border px-3 py-2.5 text-xs ${availabilityFeedback ? 'border-amber-200 bg-amber-50 text-amber-800' : 'border-border bg-muted/30 text-muted-foreground'}`}
          >
            <InfoIcon className="mt-0.5 size-3.5 shrink-0" />
            {availabilityFeedback ? (
              <span>
                <strong>{availabilityFeedback.title}:</strong> {availabilityFeedback.message}{' '}
                {availabilityFeedback.nextAction}
              </span>
            ) : (
              <span>Availability is still loading. The server will re-check it when you launch.</span>
            )}
          </div>
        )}

        <div className="space-y-1.5">
          <Label htmlFor="prompt">
            Prompt <span className="text-muted-foreground">(optional when files are attached)</span>
          </Label>
          <AttachmentComposer
            projectId={projectId!}
            value={prompt}
            onChange={setPrompt}
            onBlur={() => setPromptTouched(true)}
            placeholder="Enter your prompt for the agent..."
          />
          {showPromptError && (
            <p data-testid="prompt-error" className="text-xs text-destructive">
              Prompt is required unless at least one file is attached.
            </p>
          )}
        </div>

        {contextRefs.length > 0 && (
          <div className="space-y-1.5">
            <Label>Context References</Label>
            <div className="flex flex-wrap gap-2" data-testid="context-refs-list">
              {contextRefs.map((ref, i) => (
                <ContextRefChip key={`${ref.type}-${ref.value}`} refItem={ref} onRemove={() => removeRef(i)} />
              ))}
            </div>
          </div>
        )}

        <div className="space-y-1.5">
          <Label htmlFor="agent-select">Agent</Label>
          <AgentSelector
            agents={launchableAgents}
            selectedRef={selectedAgentRef}
            onChange={setSelectedAgentRef}
            isLoading={agentsLoading}
          />
          <p className="text-xs text-muted-foreground">
            Leave this as <span className="font-medium text-foreground">New Agent for this task</span> for a one-off
            task, or select an existing Agent to use its definition.
          </p>
          {isArchived && (
            <p data-testid="archived-warning" className="text-xs text-muted-foreground">
              This agent is archived and cannot be used to launch new sessions.
            </p>
          )}
          {selectedAgent && readinessConclusion === 'Ready' && (
            <p data-testid="agent-readiness-ready" className="text-xs text-emerald-700">
              Readiness: Ready — the server confirms this Agent can execute.
            </p>
          )}
          {selectedAgent && isNeedsSetup && (
            <div
              data-testid="agent-readiness-needs-setup"
              className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-800 space-y-1"
            >
              <p className="font-medium">Readiness: Needs setup — launch is blocked until the gaps below are fixed.</p>
              {selectedReadiness?.gaps?.length ? (
                <ul className="space-y-1">
                  {selectedReadiness.gaps.map((gap) => (
                    <li key={gap.code} data-testid={`agent-readiness-gap-${gap.code}`}>
                      <p className="font-medium">{gap.message}</p>
                      <p className="text-red-700/80">{gap.action}</p>
                    </li>
                  ))}
                </ul>
              ) : null}
              {selectedReadiness?.setup && (
                <p className="text-red-700/80">
                  Fix in{' '}
                  <a className="font-semibold underline" href={toProjectPath(selectedReadiness.setup.path)}>
                    {selectedReadiness.setup.label}
                  </a>
                  .
                </p>
              )}
            </div>
          )}
          {selectedAgent && isUnknownReadiness && (
            <p data-testid="agent-readiness-unknown-hint" className="flex items-start gap-1.5 text-xs text-amber-700">
              <InfoIcon className="mt-0.5 size-3.5 shrink-0" />
              <span>Readiness: Unknown — launch will proceed and will wait for the server to validate execution.</span>
            </p>
          )}
        </div>

        {isCreatingAgent && (
          <div data-testid="task-capability-controls" className="space-y-3 rounded-lg border border-border bg-card p-4">
            <div>
              <p className="text-sm font-medium text-foreground">Execution scope</p>
              <p className="text-xs text-muted-foreground mt-0.5">
                Choose collaborator Agents and an optional concurrency limit for the new Agent.
              </p>
            </div>
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
              <div className="space-y-1.5">
                <Label htmlFor="task-collaborators">Allowed collaborators</Label>
                <select
                  id="task-collaborators"
                  multiple
                  value={allowedCollaboratorIds}
                  onChange={(event) =>
                    setAllowedCollaboratorIds(Array.from(event.target.selectedOptions, (option) => option.value))
                  }
                  className="min-h-24 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm"
                  data-testid="task-collaborators"
                >
                  {launchableAgents.map((agent) => (
                    <option key={agent.id} value={agent.id}>
                      {agent.name}
                    </option>
                  ))}
                </select>
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="task-max-concurrent-runs">Max concurrent runs</Label>
                <Input
                  id="task-max-concurrent-runs"
                  type="number"
                  min={1}
                  step={1}
                  value={maxConcurrentRunsText}
                  onChange={(event) => setMaxConcurrentRunsText(event.target.value)}
                  placeholder="Unlimited"
                  data-testid="task-max-concurrent-runs"
                />
                {!concurrencyValid && (
                  <p className="text-xs text-destructive">Use a positive whole number or leave this empty.</p>
                )}
              </div>
            </div>
          </div>
        )}

        {isCreatingAgent && defaultExecutionConfig && !executionConfigAdjusted && (
          <div data-testid="recommended-execution-config" className="rounded-lg border border-border bg-card p-4">
            <div className="flex flex-wrap items-start justify-between gap-3">
              <div>
                <p className="text-sm font-medium text-foreground">Recommended execution configuration</p>
                <p className="text-xs text-muted-foreground mt-0.5">Project default for tasks in this Project</p>
                <p className="mt-2 text-xs text-foreground">
                  {defaultExecutionConfig.runtime === 'opencode' ? 'OpenCode' : 'Pi'} · {defaultExecutionConfig.model}
                  {defaultExecutionConfig.variant ? ` · ${defaultExecutionConfig.variant}` : ''}
                </p>
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                data-testid="adjust-execution-config"
                onClick={() => setExecutionConfigAdjusted(true)}
              >
                Adjust
              </Button>
            </div>
          </div>
        )}

        {executionControlsVisible && (
          <TaskExecutionConfigControls
            runtime={executionRuntime}
            model={executionModel}
            variant={executionVariant}
            onRuntimeChange={(runtime) => {
              setExecutionRuntime(runtime)
              setExecutionModel(null)
              setExecutionVariant(null)
            }}
            onModelChange={setExecutionModel}
            onVariantChange={setExecutionVariant}
          />
        )}

        {launchAttachmentResult && (
          <div data-testid="launch-attachment-results" className="space-y-3">
            <div>
              <p className="text-sm font-medium text-foreground">Attachments submitted</p>
              <p className="text-xs text-muted-foreground">The Agent received only the files marked accepted.</p>
            </div>
            <AttachmentResults accepted={launchAttachmentResult.accepted} rejected={launchAttachmentResult.rejected} />
            <a
              data-testid="launched-agent-link"
              className="inline-flex text-xs font-medium text-primary underline underline-offset-2"
              href={toProjectPath(`/agents/${encodeURIComponent(launchAttachmentResult.agentId)}`)}
            >
              Refine {launchAttachmentResult.agentName}
            </a>
            <Button
              type="button"
              data-testid="open-launched-session"
              onClick={() => navigate(launchAttachmentResult.sessionPath)}
            >
              Open Session
            </Button>
          </div>
        )}

        <div className="flex items-center justify-end gap-3">
          <Button variant="outline" onClick={() => navigate(toProjectPath('/agents'))}>
            Cancel
          </Button>
          <Button
            data-testid="launch-button"
            onClick={handleLaunch}
            disabled={!canLaunch}
            title={launchBlockedByReadiness ? 'Readiness is Needs setup — fix the gaps first.' : undefined}
          >
            {launchPending ? 'Launching...' : 'Launch Session'}
          </Button>
        </div>
      </div>

      <Dialog open={pendingPreflight !== null} onOpenChange={(open) => !open && setPendingPreflight(null)}>
        <DialogContent data-testid="agent-task-preflight-dialog" className="sm:max-w-lg">
          <DialogHeader>
            <DialogTitle>Confirm execution scope</DialogTitle>
            <DialogDescription>
              Review the server-resolved scope before Mohist creates the Agent and starts work.
            </DialogDescription>
          </DialogHeader>
          {pendingPreflight && (
            <div className="space-y-3 text-sm" data-testid="agent-task-preflight-scope">
              <div className="grid grid-cols-[minmax(0,1fr)_minmax(0,2fr)] gap-x-4 gap-y-2">
                <span className="text-muted-foreground">Agent</span>
                <span className="font-medium">{pendingPreflight.response.agentName}</span>
                <span className="text-muted-foreground">Execution</span>
                <span>
                  {pendingPreflight.response.execution.runtime} ·{' '}
                  {pendingPreflight.response.execution.model ?? 'unresolved'}
                  {pendingPreflight.response.execution.variant
                    ? ` · ${pendingPreflight.response.execution.variant}`
                    : ''}
                </span>
                <span className="text-muted-foreground">Workspace</span>
                <span>{pendingPreflight.response.workspace}</span>
                <span className="text-muted-foreground">Repository</span>
                <span>{pendingPreflight.response.repository ?? 'Workspace repositories'}</span>
                <span className="text-muted-foreground">Issue / Epic</span>
                <span>
                  {pendingPreflight.response.issueNumber ? `#${pendingPreflight.response.issueNumber}` : 'none'}
                  {pendingPreflight.response.epicNumber ? ` / #${pendingPreflight.response.epicNumber}` : ''}
                </span>
                <span className="text-muted-foreground">Permission scope</span>
                <span>{pendingPreflight.response.permissionScope}</span>
                <span className="text-muted-foreground">Expected impact</span>
                <span>{pendingPreflight.response.expectedImpact}</span>
              </div>
              {pendingPreflight.response.workspaceRepositories.length > 0 && (
                <p className="border-t border-border pt-2 text-xs text-muted-foreground">
                  Workspace repositories: {pendingPreflight.response.workspaceRepositories.join(', ')}
                </p>
              )}
            </div>
          )}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => setPendingPreflight(null)}>
              Cancel
            </Button>
            <Button
              type="button"
              onClick={handleConfirmPreflight}
              disabled={startTaskMutation.isPending}
              data-testid="confirm-agent-task-launch"
            >
              {startTaskMutation.isPending ? 'Launching...' : 'Confirm and launch'}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
