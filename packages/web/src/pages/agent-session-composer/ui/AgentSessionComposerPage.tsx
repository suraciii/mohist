import { useCallback, useMemo, useRef, useState, type ComponentProps, type ComponentType } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { BotIcon, ChevronDownIcon, XIcon, AlertTriangleIcon, SearchIcon, InfoIcon } from 'lucide-react'
import {
  getAgentAvailabilityFeedback,
  getAgentLaunchErrorFeedback,
  useAgentListAvailability,
  useAgents,
  useLaunchAgentSession,
} from '../../../entities/agent'
import type {
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentReadinessResult,
  AgentSessionLaunchContext,
} from '../../../entities/agent'
import { extractAttachmentIds } from '../../../entities/issue'
import { useProject, useProjectPath } from '../../../entities/project'
import { useDocumentTitle } from '../../../shared/lib/useDocumentTitle'
import { AttachmentComposer as DefaultAttachmentComposer } from '../../../shared/ui/attachment-composer'
import { AttachmentResults, type AttachmentResultAccepted, type AttachmentResultRejected } from '../../../shared/ui/attachment-results'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { Badge } from '@/shared/ui/components/badge'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'
import { cn } from '@/shared/lib/utils'

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
          <Button
            variant="outline"
            data-testid="agent-selector-trigger"
            className="w-full justify-between"
          >
            {selectedAgent ? (
              <span className="truncate">{selectedAgent.name}</span>
            ) : (
              <span className="text-muted-foreground">Select an agent...</span>
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
          {filtered.length === 0 && (
            <div className="px-3 py-4 text-center text-sm text-muted-foreground">
              No agents found
            </div>
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

export interface AgentSessionComposerPageComponents {
  AttachmentComposer: ComponentType<ComponentProps<typeof DefaultAttachmentComposer>>
}

export interface AgentSessionComposerData {
  agents: AgentInfo[] | undefined
  agentsLoading: boolean
  availability: AgentAvailabilitySummaryEntry[] | undefined
  availabilityLoading: boolean
  launchMutation: Pick<ReturnType<typeof useLaunchAgentSession>, 'mutate' | 'isPending' | 'error'>
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
  const { projectId } = useProject()
  const [searchParams] = useSearchParams()

  const { agents, agentsLoading, availability, availabilityLoading, launchMutation } = dataHook()

  const launchableAgents = useMemo(
    () => agents?.filter((a) => a.status !== 'archived') ?? [],
    [agents],
  )

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
  const [launchAttachmentResult, setLaunchAttachmentResult] = useState<{
    accepted: AttachmentResultAccepted[]
    rejected: AttachmentResultRejected[]
    sessionPath: string
  } | null>(null)
  const launchKeyRef = useRef<string | null>(null)

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

  const canLaunch = (!promptEmpty || attachmentIds.length > 0)
    && !!selectedAgentRef
    && !isArchived
    && !launchBlockedByReadiness
    && !launchMutation.isPending

  const removeRef = useCallback((index: number) => {
    setContextRefs((prev) => prev.filter((_, i) => i !== index))
  }, [])

  const handleLaunch = useCallback(() => {
    if (!canLaunch || !selectedAgent) return

    const context: AgentSessionLaunchContext = {}
    for (const ref of contextRefs) {
      const number = Number(ref.value)
      if ((ref.type === 'issue' || ref.type === 'epic') && Number.isInteger(number) && number > 0) {
        if (ref.type === 'issue') context.issueNumber = number
        else context.epicNumber = number
      }
      else if (ref.type === 'repository') context.repository = ref.value
      else if (ref.type === 'workspace') context.workspacePath = ref.value
    }
    const hasContext = Object.keys(context).length > 0

    launchMutation.mutate(
      {
        agentRef: selectedAgentRef,
        prompt: prompt.trim(),
        context: hasContext ? context : null,
        attachments: attachmentIds,
        idempotencyKey: launchKeyRef.current ??= crypto.randomUUID(),
      },
      {
        onSuccess: (data) => {
          const fallbackJobQuery = data.jobId ? `?jobId=${encodeURIComponent(data.jobId)}` : ''
          const sessionPath = data.sessionUrl ?? `${toProjectPath(`/sessions/${encodeURIComponent(data.sessionId)}`)}${fallbackJobQuery}`
          const accepted = data.attachments ?? []
          const rejected = data.rejectedAttachments ?? []
          launchKeyRef.current = null
          if (accepted.length > 0 || rejected.length > 0) {
            setLaunchAttachmentResult({ accepted, rejected, sessionPath })
            return
          }
          navigate(sessionPath)
        },
      },
    )
  }, [attachmentIds, canLaunch, selectedAgent, selectedAgentRef, contextRefs, prompt, launchMutation, navigate, toProjectPath])

  const launchError = launchMutation.error
  const launchFeedback = getAgentLaunchErrorFeedback(launchError, selectedReadiness)
  const isNeedsSetupError = launchFeedback?.kind === 'needs-setup'
  const launchErrorData = launchError && 'data' in launchError
    ? (launchError as {
      data?: {
        gaps?: Array<{ code?: string; message?: string; action?: string }>
        setup?: { label?: string; path?: string } | null
      }
    }).data
    : undefined
  const gapsFromError = isNeedsSetupError
    ? (launchError && 'data' in launchError
      ? launchErrorData?.gaps
      : undefined) ?? selectedReadiness?.gaps
    : undefined
  const setupFromError = isNeedsSetupError && launchErrorData?.setup?.label && launchErrorData.setup.path
    ? launchErrorData.setup
    : selectedReadiness?.setup

  const availabilityFeedback = selectedAvailability && !selectedAvailability.canStartNow
    ? getAgentAvailabilityFeedback(selectedAvailability.waitingReason)
    : undefined
  const availabilityFeedbackLoading = !selectedAvailability && availabilityLoading
  const launchErrorTestId = launchFeedback?.kind === 'runner-offline'
    ? 'error-no-runner'
    : launchFeedback?.kind === 'execution-unavailable' && launchError && 'code' in launchError && (launchError as { code?: string }).code === 'EXTERNAL_AGENT_UNAVAILABLE'
      ? 'error-external-agent'
      : launchFeedback?.kind === 'needs-setup'
        ? 'error-needs-setup'
        : launchFeedback?.kind === 'back-pressure'
          ? 'error-back-pressure'
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
            className={`flex flex-col gap-1 rounded-lg border px-3 py-2.5 text-sm ${launchFeedback.kind === 'needs-setup' ? 'border-red-200 bg-red-50 text-red-800' : launchFeedback.kind === 'execution-unavailable' ? 'border-red-200 bg-red-50 text-red-800' : 'border-amber-200 bg-amber-50 text-amber-800'}`}
          >
            <div className="flex items-start gap-2">
              <AlertTriangleIcon className="mt-0.5 size-4 shrink-0" />
              <span className="font-medium">{launchFeedback.title}</span>
            </div>
            <p className="ml-6 text-xs">{launchFeedback.message} {launchFeedback.nextAction}</p>
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
                Fix in <a className="font-semibold underline" href={toProjectPath(setupFromError.path)}>{setupFromError.label}</a>.
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
              <span><strong>{availabilityFeedback.title}:</strong> {availabilityFeedback.message} {availabilityFeedback.nextAction}</span>
            ) : (
              <span>Availability is still loading. The server will re-check it when you launch.</span>
            )}
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
              <p className="font-medium">
                Readiness: Needs setup — launch is blocked until the gaps below are fixed.
              </p>
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
                  Fix in <a className="font-semibold underline" href={toProjectPath(selectedReadiness.setup.path)}>{selectedReadiness.setup.label}</a>.
                </p>
              )}
            </div>
          )}
          {selectedAgent && isUnknownReadiness && (
            <p
              data-testid="agent-readiness-unknown-hint"
              className="flex items-start gap-1.5 text-xs text-amber-700"
            >
              <InfoIcon className="mt-0.5 size-3.5 shrink-0" />
              <span>
                Readiness: Unknown — launch will proceed and will wait for the server to validate execution.
              </span>
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

        {launchAttachmentResult && (
          <div data-testid="launch-attachment-results" className="space-y-3">
            <div>
              <p className="text-sm font-medium text-foreground">Attachments submitted</p>
              <p className="text-xs text-muted-foreground">The Agent received only the files marked accepted.</p>
            </div>
            <AttachmentResults
              accepted={launchAttachmentResult.accepted}
              rejected={launchAttachmentResult.rejected}
            />
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
          <Button
            variant="outline"
            onClick={() => navigate(toProjectPath('/agents'))}
          >
            Cancel
          </Button>
          <Button
            data-testid="launch-button"
            onClick={handleLaunch}
            disabled={!canLaunch}
            title={launchBlockedByReadiness ? 'Readiness is Needs setup — fix the gaps first.' : undefined}
          >
            {launchMutation.isPending ? 'Launching...' : 'Launch Session'}
          </Button>
        </div>
      </div>
    </div>
  )
}
