import { useCallback, useMemo, useRef, useState, type ComponentProps, type ComponentType } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { BotIcon, ChevronDownIcon, XIcon, AlertTriangleIcon, SearchIcon, InfoIcon, PlusIcon, RefreshCwIcon } from 'lucide-react'
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
import type { IssueListItem } from '../../../entities/issue'
import { useIssues } from '../../../entities/issue'
import type { EpicWithProgress } from '../../../entities/epic'
import { useEpics } from '../../../entities/epic'
import { useProject, useProjectPath } from '../../../entities/project'
import type { Repository } from '../../../entities/project'
import { useRepositories } from '../../../entities/project'
import type { Workspace } from '../../../entities/workspace'
import { useWorkspaces } from '../../../entities/workspace'
import { CreateWorkspaceDialog } from '../../../features/create-workspace'
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
  workspaceField?: 'workspace' | 'workspacePath'
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

function ContextPicker({
  contextRefs,
  repositories,
  workspaces,
  workspacesError,
  issues,
  epics,
  loading,
  onChange,
  onCreateWorkspace,
}: {
  contextRefs: ContextRef[]
  repositories: Repository[]
  workspaces: Workspace[]
  issues: IssueListItem[]
  epics: EpicWithProgress[]
  loading: boolean
  workspacesError: boolean
  onChange: (type: ContextRef['type'], value: string) => void
  onCreateWorkspace: () => void
}) {
  const selectedValue = (type: ContextRef['type']) => contextRefs.find((item) => item.type === type)?.value ?? ''
  const selectClass = 'h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm shadow-sm'

  return (
    <div className="grid gap-3 sm:grid-cols-2" data-testid="launch-context-picker">
      <div className="space-y-1.5">
        <Label htmlFor="launch-repository">Repository</Label>
        <select
          id="launch-repository"
          aria-label="Repository"
          data-testid="launch-repository"
          className={selectClass}
          value={selectedValue('repository')}
          onChange={(event) => onChange('repository', event.target.value)}
          disabled={loading}
        >
          <option value="">No repository selected</option>
          {repositories.map((repository) => <option key={repository.name} value={repository.name}>{repository.name}</option>)}
        </select>
      </div>
      <div className="space-y-1.5">
        <div className="flex items-center justify-between gap-2">
          <Label htmlFor="launch-workspace">Workspace</Label>
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={onCreateWorkspace}
            disabled={loading}
            data-testid="create-workspace-from-composer"
          >
            <PlusIcon className="mr-1 h-3.5 w-3.5" aria-hidden="true" />
            Create
          </Button>
        </div>
        <select
          id="launch-workspace"
          aria-label="Workspace"
          data-testid="launch-workspace"
          className={selectClass}
          value={selectedValue('workspace')}
          onChange={(event) => onChange('workspace', event.target.value)}
          disabled={loading}
        >
          <option value="">No workspace selected</option>
          {workspaces.map((workspace) => <option key={workspace.name} value={workspace.name}>{workspace.name}</option>)}
        </select>
        {workspaces.length === 0 && !loading && !workspacesError && (
          <p className="text-xs text-muted-foreground" data-testid="composer-no-workspaces">
            No active workspaces.
          </p>
        )}
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="launch-issue">Issue</Label>
        <select
          id="launch-issue"
          aria-label="Issue"
          data-testid="launch-issue"
          className={selectClass}
          value={selectedValue('issue')}
          onChange={(event) => onChange('issue', event.target.value)}
          disabled={loading}
        >
          <option value="">No Issue selected</option>
          {issues.map((issue) => <option key={issue.number} value={String(issue.number)}>#{issue.number} {issue.title}</option>)}
        </select>
      </div>
      <div className="space-y-1.5">
        <Label htmlFor="launch-epic">Epic</Label>
        <select
          id="launch-epic"
          aria-label="Epic"
          data-testid="launch-epic"
          className={selectClass}
          value={selectedValue('epic')}
          onChange={(event) => onChange('epic', event.target.value)}
          disabled={loading}
        >
          <option value="">No Epic selected</option>
          {epics.map((epic) => <option key={epic.number} value={String(epic.number)}>#{epic.number} {epic.title}</option>)}
        </select>
      </div>
    </div>
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
  repositories?: Repository[]
  repositoriesError?: boolean
  retryRepositories?: () => void | Promise<unknown>
  workspaces?: Workspace[]
  workspacesError?: boolean
  retryWorkspaces?: () => void | Promise<unknown>
  issues?: IssueListItem[]
  epics?: EpicWithProgress[]
  contextLoading?: boolean
}

export type AgentSessionComposerDataHook = () => AgentSessionComposerData

const useDefaultData: AgentSessionComposerDataHook = () => {
  const { projectId } = useProject()
  const { data: agents, isLoading: agentsLoading } = useAgents()
  const { data: availability, isLoading: availabilityLoading } = useAgentListAvailability()
  const {
    data: repositories,
    isLoading: repositoriesLoading,
    isError: repositoriesError,
    refetch: refetchRepositories,
  } = useRepositories(projectId ?? undefined)
  const {
    data: workspaces,
    isLoading: workspacesLoading,
    isError: workspacesError,
    refetch: refetchWorkspaces,
  } = useWorkspaces('active')
  const { data: issues, isLoading: issuesLoading } = useIssues({ projectId: projectId ?? undefined, all: false })
  const { data: epics, isLoading: epicsLoading } = useEpics()
  return {
    agents,
    agentsLoading,
    availability,
    availabilityLoading,
    launchMutation: useLaunchAgentSession(),
    repositories,
    repositoriesError,
    retryRepositories: () => refetchRepositories(),
    workspaces,
    workspacesError,
    retryWorkspaces: () => refetchWorkspaces(),
    issues,
    epics,
    contextLoading: repositoriesLoading || workspacesLoading || issuesLoading || epicsLoading,
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

  const {
    agents,
    agentsLoading,
    availability,
    availabilityLoading,
    launchMutation,
    repositories = [],
    repositoriesError = false,
    retryRepositories,
    workspaces = [],
    workspacesError = false,
    retryWorkspaces,
    issues = [],
    epics = [],
    contextLoading = false,
  } = dataHook()

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
    const workspace = searchParams.get('workspace')
    const ws = searchParams.get('ws')
    if (workspace) {
      refs.push({ type: 'workspace', label: `Workspace: ${workspace}`, value: workspace, workspaceField: 'workspace' })
    } else if (ws) {
      refs.push({ type: 'workspace', label: `Workspace: ${ws}`, value: ws, workspaceField: 'workspacePath' })
    }
    return refs
  })

  const [prompt, setPrompt] = useState('')
  const [promptTouched, setPromptTouched] = useState(false)
  const [createWorkspaceOpen, setCreateWorkspaceOpen] = useState(false)
  const [createdWorkspace, setCreatedWorkspace] = useState<Workspace | null>(null)
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

  const removeRef = useCallback((index: number) => {
    setContextRefs((prev) => prev.filter((_, i) => i !== index))
  }, [])

  const updateContextRef = useCallback((type: ContextRef['type'], value: string) => {
    setContextRefs((previous) => {
      const withoutType = previous.filter((item) => item.type !== type)
      if (!value) return withoutType
      const label = type === 'issue'
        ? `Issue #${value}`
        : type === 'epic'
          ? `Epic: ${value}`
          : type === 'repository'
            ? `Repository: ${value}`
            : `Workspace: ${value}`
      return [...withoutType, { type, label, value, ...(type === 'workspace' ? { workspaceField: 'workspace' as const } : {}) }]
    })
  }, [])

  const contextByType = useMemo(
    () => new Map(contextRefs.map((ref) => [ref.type, ref] as const)),
    [contextRefs],
  )
  const activeWorkspaces = useMemo(
    () => {
      const active = workspaces.filter((workspace) => workspace.status === 'active')
      if (!createdWorkspace || active.some((workspace) => workspace.name === createdWorkspace.name)) return active
      return [...active, createdWorkspace]
    },
    [createdWorkspace, workspaces],
  )
  const selectedRepository = contextByType.get('repository')?.value ?? null
  const selectedWorkspaceRef = contextByType.get('workspace')
  const selectedWorkspace = selectedWorkspaceRef?.workspaceField === 'workspace'
    ? selectedWorkspaceRef.value
    : null
  const selectedWorkspaceEntry = activeWorkspaces.find((workspace) => workspace.name === selectedWorkspace)
  const compatibleRepositories = useMemo(
    () => selectedWorkspaceEntry
      ? repositories.filter((repository) => selectedWorkspaceEntry.repositories.some(
        (name) => name.localeCompare(repository.name, undefined, { sensitivity: 'accent' }) === 0,
      ))
      : [],
    [repositories, selectedWorkspaceEntry],
  )
  const repositoryScopeCompatible = !selectedRepository || Boolean(
    selectedWorkspaceEntry?.repositories.some(
      (name) => name.localeCompare(selectedRepository, undefined, { sensitivity: 'accent' }) === 0,
    ),
  )
  const initialWorkspaceRepositoryNames = useMemo(
    () => selectedRepository ? [selectedRepository] : [],
    [selectedRepository],
  )
  const handleWorkspaceCreated = useCallback((workspace: Workspace) => {
    setCreatedWorkspace(workspace)
    const keepRepository = selectedRepository !== null && workspace.repositories.some(
      (name) => name.localeCompare(selectedRepository, undefined, { sensitivity: 'accent' }) === 0,
    )
    setContextRefs((previous) => [
      ...previous.filter((item) => item.type !== 'workspace' && (item.type !== 'repository' || keepRepository)),
      { type: 'workspace', label: `Workspace: ${workspace.name}`, value: workspace.name, workspaceField: 'workspace' },
    ])
  }, [selectedRepository])
  const selectedIssue = contextByType.get('issue')?.value ?? null
  const selectedEpic = contextByType.get('epic')?.value ?? null
  const promptEmpty = !prompt.trim()
  const attachmentIds = useMemo(() => extractAttachmentIds(prompt), [prompt])
  const showPromptError = promptTouched && promptEmpty && attachmentIds.length === 0
  const workspaceScopeConfirmed = Boolean(selectedWorkspaceEntry)
  const canLaunch = (!promptEmpty || attachmentIds.length > 0)
    && !!selectedAgentRef
    && !isArchived
    && !launchBlockedByReadiness
    && workspaceScopeConfirmed
    && repositoryScopeCompatible
    && !repositoriesError
    && !workspacesError
    && !launchMutation.isPending

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
      else if (ref.type === 'workspace') {
        if (ref.workspaceField === 'workspace') context.workspace = ref.value
        else context.workspacePath = ref.value
      }
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

        <div className="space-y-3 rounded-lg border border-border bg-card p-4">
          <div>
            <h2 className="text-sm font-medium text-foreground">Execution context</h2>
            <p className="mt-0.5 text-xs text-muted-foreground">Choose the facts that should be attached to this Job.</p>
          </div>
          <ContextPicker
            contextRefs={contextRefs}
            repositories={compatibleRepositories}
            workspaces={activeWorkspaces}
            workspacesError={workspacesError}
            issues={issues}
            epics={epics}
            loading={contextLoading}
            onChange={updateContextRef}
            onCreateWorkspace={() => setCreateWorkspaceOpen(true)}
          />
          {repositoriesError && (
            <div
              role="alert"
              data-testid="composer-repositories-error"
              className="flex items-center justify-between gap-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-900"
            >
              <span className="flex items-center gap-1.5">
                <AlertTriangleIcon className="size-3.5 shrink-0" />
                Repositories failed to load.
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => retryRepositories?.()}
                data-testid="retry-composer-repositories"
              >
                <RefreshCwIcon className="size-3.5" />
                Retry
              </Button>
            </div>
          )}
          {workspacesError && (
            <div
              role="alert"
              data-testid="composer-workspaces-error"
              className="flex items-center justify-between gap-2 rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-900"
            >
              <span className="flex items-center gap-1.5">
                <AlertTriangleIcon className="size-3.5 shrink-0" />
                Workspaces failed to load.
              </span>
              <Button
                type="button"
                variant="outline"
                size="sm"
                onClick={() => retryWorkspaces?.()}
                data-testid="retry-composer-workspaces"
              >
                <RefreshCwIcon className="size-3.5" />
                Retry
              </Button>
            </div>
          )}
          {selectedAgent && !workspaceScopeConfirmed && (
            <div
              data-testid="workspace-scope-blocked"
              className="flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900"
            >
              <AlertTriangleIcon className="mt-0.5 size-3.5 shrink-0" />
              <span>Workspace scope is required. Select an active Workspace before launching this Job.</span>
            </div>
          )}
          {selectedAgent && selectedRepository && !repositoryScopeCompatible && (
            <div
              data-testid="repository-scope-blocked"
              className="flex items-start gap-2 rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900"
            >
              <AlertTriangleIcon className="mt-0.5 size-3.5 shrink-0" />
              <span>{selectedWorkspaceEntry
                ? 'Repository is not attached to the selected Workspace. Choose a compatible repository before launching.'
                : 'Select an active Workspace before choosing a Repository.'}</span>
            </div>
          )}
          {contextRefs.length > 0 && (
            <div className="flex flex-wrap gap-2 border-t border-border pt-3" data-testid="context-refs-list">
              {contextRefs.map((ref, i) => (
                <ContextRefChip key={`${ref.type}-${ref.value}`} refItem={ref} onRemove={() => removeRef(i)} />
              ))}
            </div>
          )}
        </div>

        <div className="rounded-lg border border-blue-200 bg-blue-50/60 p-4" data-testid="launch-scope-review">
          <div className="flex items-center justify-between gap-3">
            <h2 className="text-sm font-medium text-blue-950">Start session review</h2>
            <Badge variant="outline" className="border-blue-300 text-[10px] text-blue-800">New Job only</Badge>
          </div>
          <dl className="mt-3 grid gap-x-4 gap-y-2 text-xs sm:grid-cols-2">
            <div><dt className="text-blue-800/70">Agent</dt><dd data-testid="scope-agent" className="font-medium text-blue-950">{selectedAgent?.name ?? 'Not selected'}</dd></div>
            <div><dt className="text-blue-800/70">Repository</dt><dd data-testid="scope-repository" className="font-medium text-blue-950">{selectedRepository ?? 'Not selected'}</dd></div>
            <div><dt className="text-blue-800/70">Workspace</dt><dd data-testid="scope-workspace" className="font-medium text-blue-950">{selectedWorkspaceEntry?.name ?? (selectedWorkspaceRef ? `${selectedWorkspaceRef.value} (not confirmed)` : 'Not selected; choose an active Workspace')}</dd></div>
            <div><dt className="text-blue-800/70">Issue</dt><dd data-testid="scope-issue" className="font-medium text-blue-950">{selectedIssue ? `#${selectedIssue}` : 'Not selected'}</dd></div>
            <div><dt className="text-blue-800/70">Epic</dt><dd data-testid="scope-epic" className="font-medium text-blue-950">{selectedEpic ? `#${selectedEpic}` : 'Not selected'}</dd></div>
            <div><dt className="text-blue-800/70">Permission impact</dt><dd data-testid="scope-permissions" className="font-medium text-blue-950">{selectedWorkspaceEntry ? `Runtime-managed access in ${selectedWorkspaceEntry.name}` : 'Cannot review access until a Workspace is selected'}</dd></div>
          </dl>
          <p className="mt-3 border-t border-blue-200 pt-2 text-[10px] leading-relaxed text-blue-900/80">
            Saving the Agent changes future Jobs only. This review records launch facts; it does not change the Agent definition or claim a Server permission policy.
          </p>
        </div>

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
            title={launchBlockedByReadiness
              ? 'Readiness is Needs setup — fix the gaps first.'
              : !workspaceScopeConfirmed
                ? 'Select an active Workspace before launching.'
                : !repositoryScopeCompatible
                  ? 'Select a repository attached to the Workspace.'
                  : undefined}
          >
            {launchMutation.isPending ? 'Launching...' : 'Launch Session'}
          </Button>
        </div>
        {createWorkspaceOpen && (
          <CreateWorkspaceDialog
            open
            onClose={() => setCreateWorkspaceOpen(false)}
            initialRepositoryNames={initialWorkspaceRepositoryNames}
            onCreated={handleWorkspaceCreated}
          />
        )}
      </div>
    </div>
  )
}
