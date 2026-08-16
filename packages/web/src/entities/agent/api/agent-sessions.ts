import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type {
  AgentSessionActivity,
  AgentSessionTranscriptResponse,
  AgentSessionUsage,
  AgentTurnObservation,
  SessionFollowupResult,
  SessionInputObservation,
} from '../../coder-session/@x/agent-session'
import { useProject } from '../../project/@x/project-context'
import { projectApiPath, request } from '../../../shared/api/client'
import { createIdempotencyKey } from '../../../shared/lib/idempotency-key'

/* ── DTO types (matching #129/#130 server contracts) ────── */

export interface AgentSessionListContextRefsDto {
  issueNumber?: number | null
  epicNumber?: number | null
  repository?: string | null
  workspacePath?: string | null
  workspaceName?: string | null
}

export interface AgentSessionListItemDto {
  sessionId: string
  agentId: string
  agentName: string
  activity?: AgentSessionActivity
  status?: string
  createdAt: string
  lastActivityAt: string | null
  resolvedModel: string | null
  contextRefs?: AgentSessionListContextRefsDto | null
  origin?: string | null
  targetId?: string | null
}

export interface GenericAgentSessionSummaryDto {
  sessionId: string
  agentId: string
  agentName: string
  runtimeSessionId: string | null
  runtime: string | null
  activity: AgentSessionActivity
  createdAt: string
  lastActivityAt: string | null
  resolvedModel: string | null
  failureCategory: string | null
  failureReason: string | null
  toolCallCount: number | null
  toolErrorCount: number | null
  contextRefs: AgentSessionListContextRefsDto | null
  usage: AgentSessionUsage
  recoveryAvailable: boolean
  currentTurnId?: string | null
  inputs?: SessionInputObservation[] | null
  turns?: AgentTurnObservation[] | null
  origin?: string | null
  targetId?: string | null
}

export interface AgentSessionLaunchResponse {
  jobId?: string | null
  sessionId: string
  inputId?: string | null
  turnId?: string | null
  agentId: string
  agentName: string
  workspaceId: string
  targetId: string
  origin: string
  status: string
  attachments?: AgentSessionAttachment[] | null
  rejectedAttachments?: AgentSessionAttachmentRejection[] | null
  transcriptUrl: string
  jobUrl?: string | null
  observationUrl?: string | null
  sessionUrl?: string | null
}

export interface AgentSessionAttachment {
  id: string
  name: string
  contentType?: string | null
  size: number
}

export interface AgentSessionAttachmentRejection {
  id: string
  reason: string
  message: string
}

export interface AgentSessionLaunchContext {
  issueNumber?: number | null
  epicNumber?: number | null
  repository?: string | null
  workspace?: string | null
  workspacePath?: string | null
}

export interface AgentSessionLaunchInput {
  prompt: string
  context?: AgentSessionLaunchContext | null
  attachments?: string[]
}

export interface AgentTaskLaunchInput extends AgentSessionLaunchInput {
  name?: string | null
  runtime?: string | null
  model?: string | null
  variant?: string | null
  allowedSubagentAgentIds?: string[] | null
  maxConcurrentRuns?: number | null
  preflightFingerprint?: string | null
}

export interface AgentTaskPreflightResponse {
  scopeFingerprint: string
  agentName: string
  execution: {
    runtime: 'opencode' | 'pi'
    model: string | null
    variant: string | null
  }
  repository: string | null
  workspace: string
  workspaceRepositories: string[]
  issueNumber: number | null
  epicNumber: number | null
  permissionScope: string
  expectedImpact: string
}

export interface AgentLaunchObservationDto {
  jobId: string
  jobStatus: string
  jobMessage?: string | null
  jobOutput?: string | null
  jobFailureReason?: string | null
  jobExitCode?: number | null
  sessionId: string
  sessionActivity: AgentSessionActivity
  sessionRuntime?: string | null
  transcriptUrl: string
  inputId?: string | null
  inputAcceptance: string
  turnId?: string | null
  turnStatus: string
  turnResult?: {
    message?: string | null
    output?: string | null
    failureReason?: string | null
    failureCategory?: string | null
    exitCode?: number | null
  } | null
  observationUrl: string
}

export type AgentLaunchObservationMeaning = 'observe' | 'result' | 'reconcile'

export function getAgentLaunchObservationMeaning(
  observation: Pick<AgentLaunchObservationDto, 'turnStatus'>,
): AgentLaunchObservationMeaning {
  const status = observation.turnStatus.toLowerCase()
  if (status === 'unknown') return 'reconcile'
  if (status === 'completed' || status === 'failed') return 'result'
  return 'observe'
}

export interface GenericFollowupInput {
  text: string
  attachments?: string[]
}

export interface GenericFollowupResult {
  status: string
  inputId?: string | null
  turnId?: string | null
  attachments?: AgentSessionAttachment[] | null
  rejectedAttachments?: AgentSessionAttachmentRejection[] | null
  error?: string | null
  code?: string | null
}

export type TurnControlState = 'cancelled' | 'stop-requested' | 'stopped' | 'unknown' | 'not-cancellable'
export interface TurnControlResult {
  state?: string
  interruptUnconfirmed?: boolean | null
}

/* ── Pure client functions ──────────────────────────────── */

export function getAgentSessions(projectId: string, agentRef: string, params?: { status?: string; limit?: number }) {
  const search = new URLSearchParams()
  if (params?.status) search.set('status', params.status)
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<AgentSessionListItemDto[]>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/sessions${qs ? `?${qs}` : ''}`),
  )
}

export function getGenericSessionSummary(projectId: string, sessionId: string) {
  return request<GenericAgentSessionSummaryDto>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}`),
  )
}

export function getGenericSessionTranscript(projectId: string, sessionId: string, runtimeSessionId?: string | null) {
  const search = runtimeSessionId ? `?${new URLSearchParams({ runtimeSessionId }).toString()}` : ''
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/transcript${search}`),
  )
}

export function agentInputAttachmentContentPath(
  projectId: string | null | undefined,
  sessionId: string,
  inputId: string,
  attachmentId: string,
) {
  return projectApiPath(
    projectId,
    `/agent-sessions/${encodeURIComponent(sessionId)}/inputs/${encodeURIComponent(inputId)}/attachments/${encodeURIComponent(attachmentId)}/content`,
  )
}

export function launchAgentSession(
  projectId: string,
  agentRef: string,
  input: AgentSessionLaunchInput,
  idempotencyKey?: string,
) {
  return request<AgentSessionLaunchResponse>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/sessions`),
    {
      method: 'POST',
      body: JSON.stringify(input),
      headers: {
        'X-Mohist-Launch-Origin': 'web',
        ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}),
      },
    },
  )
}

export function preflightAgentTask(projectId: string, input: AgentTaskLaunchInput, idempotencyKey: string) {
  const { preflightFingerprint: _ignored, ...body } = input
  return request<AgentTaskPreflightResponse>(projectApiPath(projectId, '/agent-tasks/preflight'), {
    method: 'POST',
    body: JSON.stringify(body),
    headers: {
      'X-Mohist-Launch-Origin': 'web',
      'Idempotency-Key': idempotencyKey,
    },
  })
}

export function startAgentTask(projectId: string, input: AgentTaskLaunchInput, idempotencyKey?: string) {
  const { preflightFingerprint, ...body } = input
  return request<AgentSessionLaunchResponse>(projectApiPath(projectId, '/agent-tasks'), {
    method: 'POST',
    body: JSON.stringify(body),
    headers: {
      'X-Mohist-Launch-Origin': 'web',
      ...(idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : {}),
      ...(preflightFingerprint ? { 'X-Mohist-Agent-Preflight': preflightFingerprint } : {}),
    },
  })
}

export function getAgentLaunchObservation(projectId: string, jobId: string) {
  return request<AgentLaunchObservationDto>(
    projectApiPath(projectId, `/agent-jobs/${encodeURIComponent(jobId)}/launch-observation`),
  )
}

export function launchObservationQueryOptions(projectId: string | null | undefined, jobId: string | null | undefined) {
  return {
    queryKey: ['agent-launch-observation', projectId, jobId],
    queryFn: () => getAgentLaunchObservation(projectId!, jobId!),
    enabled: !!projectId && !!jobId,
    refetchInterval: 5000,
  }
}

export function postGenericFollowup(
  projectId: string,
  sessionId: string,
  input: GenericFollowupInput,
  idempotencyKey?: string,
) {
  const requestKey = idempotencyKey ?? createIdempotencyKey()
  return request<SessionFollowupResult>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/followup`),
    {
      method: 'POST',
      body: JSON.stringify(input),
      headers: { 'Idempotency-Key': requestKey },
    },
  )
}

export function controlGenericSession(projectId: string, sessionId: string, turnId: string, idempotencyKey?: string) {
  const requestKey = idempotencyKey ?? createIdempotencyKey()
  return request<TurnControlResult>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/stop`),
    {
      method: 'POST',
      body: JSON.stringify({ turnId }),
      headers: { 'Idempotency-Key': requestKey },
    },
  )
}

export function stopGenericSession(projectId: string, sessionId: string, turnId: string, idempotencyKey?: string) {
  return controlGenericSession(projectId, sessionId, turnId, idempotencyKey)
}

/* ── Query hooks ────────────────────────────────────────── */

export function genericSessionSummaryQueryOptions(projectId: string | null | undefined, sessionId: string) {
  return {
    queryKey: ['agent-session', projectId, sessionId],
    queryFn: () => getGenericSessionSummary(projectId!, sessionId),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query: { state: { data: GenericAgentSessionSummaryDto | undefined } }) => {
      const data = query.state.data
      if (!data) return 5000
      return data.activity === 'idle' ? false : 5000
    },
  }
}

export function useGenericSessionSummary(sessionId: string) {
  const { projectId } = useProject()
  return useQuery<GenericAgentSessionSummaryDto>(genericSessionSummaryQueryOptions(projectId, sessionId))
}

export function genericSessionTranscriptQueryOptions(
  projectId: string | null | undefined,
  sessionId: string,
  runtimeSessionId?: string | null,
) {
  return {
    queryKey: ['agent-session', projectId, sessionId, 'transcript', runtimeSessionId ?? null],
    queryFn: () => getGenericSessionTranscript(projectId!, sessionId, runtimeSessionId),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query: { state: { data: AgentSessionTranscriptResponse | undefined } }) => {
      const data = query.state.data
      if (!data || !data.turns) return 5000
      const hasIncomplete = data.turns.some((t) => t.incomplete)
      return hasIncomplete ? 5000 : false
    },
  }
}

export function useGenericSessionTranscript(sessionId: string, runtimeSessionId?: string | null) {
  const { projectId } = useProject()
  return useQuery<AgentSessionTranscriptResponse>(
    genericSessionTranscriptQueryOptions(projectId, sessionId, runtimeSessionId),
  )
}

/* ── Mutation hooks ─────────────────────────────────────── */

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function launchAgentSessionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({
      agentRef,
      prompt,
      context,
      attachments,
      idempotencyKey,
    }: {
      agentRef: string
      prompt: string
      context?: AgentSessionLaunchContext | null
      attachments?: string[]
      idempotencyKey?: string
    }) => launchAgentSession(projectId!, agentRef, { prompt, context, attachments }, idempotencyKey),
    onSuccess: (
      _data: AgentSessionLaunchResponse,
      variables: {
        agentRef: string
        prompt: string
        context?: AgentSessionLaunchContext | null
        attachments?: string[]
      },
    ) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-availability', projectId] })
      queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      toast.success('Session launched')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useLaunchAgentSession() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(launchAgentSessionMutationOptions(projectId, queryClient))
}

export function preflightAgentTaskMutationOptions(projectId: string | null | undefined) {
  return {
    mutationFn: ({ idempotencyKey, ...input }: AgentTaskLaunchInput & { idempotencyKey: string }) =>
      preflightAgentTask(projectId!, input, idempotencyKey),
  }
}

export function usePreflightAgentTask() {
  const { projectId } = useProject()
  return useMutation(preflightAgentTaskMutationOptions(projectId))
}

export function startAgentTaskMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ idempotencyKey, ...input }: AgentTaskLaunchInput & { idempotencyKey?: string }) =>
      startAgentTask(projectId!, input, idempotencyKey),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-availability', projectId] })
      queryClient.invalidateQueries({ queryKey: ['agents', projectId] })
      toast.success('Task launched')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useStartAgentTask() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(startAgentTaskMutationOptions(projectId, queryClient))
}

export function genericFollowupMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({
      sessionId,
      text,
      attachments,
      idempotencyKey,
    }: {
      sessionId: string
      text: string
      attachments?: string[]
      idempotencyKey?: string
    }) => postGenericFollowup(projectId!, sessionId, { text, attachments }, idempotencyKey),
    onSuccess: (
      data: SessionFollowupResult,
      variables: { sessionId: string; text: string; attachments?: string[]; agentRef?: string },
    ) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId, 'transcript'] })
      if (variables.agentRef) {
        queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      }
      if (data.status === 'accepted') {
        toast.success('Follow-up sent')
        return
      }
      if (data.status === 'rejected') {
        toast.error(data.error ?? 'Follow-up rejected')
        return
      }
      toast.warning('Follow-up outcome is unknown. Retry with the same key.')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useGenericFollowup() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(genericFollowupMutationOptions(projectId, queryClient))
}

export function stopGenericSessionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({
      sessionId,
      turnId,
      idempotencyKey,
    }: {
      sessionId: string
      turnId: string
      idempotencyKey?: string
      agentRef?: string
    }) => stopGenericSession(projectId!, sessionId, turnId, idempotencyKey),
    onSuccess: (data: TurnControlResult, variables: { sessionId: string; turnId: string; agentRef?: string }) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId] })
      if (variables.agentRef) {
        queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      }
      const state = data?.state
      if (state && ['cancelled', 'stop-requested', 'stopped', 'unknown'].includes(state)) {
        toast.success(state)
        return
      }
      toast.warning('Turn stop was not applied')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export const genericTurnControlMutationOptions = stopGenericSessionMutationOptions

export function useGenericTurnControl() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(genericTurnControlMutationOptions(projectId, queryClient))
}
