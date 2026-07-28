import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type {
  AgentSessionActivity,
  AgentSessionTranscriptResponse,
  AgentSessionUsage,
} from '../../coder-session/@x/agent-session'
import { useProject } from '../../project/@x/project-context'
import { projectApiPath, request } from '../../../shared/api/client'

/* ── DTO types (matching #129/#130 server contracts) ────── */

export interface AgentSessionListContextRefsDto {
  issueNumber?: number | null
  epicNumber?: number | null
  repository?: string | null
  workspacePath?: string | null
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
}

export interface AgentSessionLaunchResponse {
  jobId?: string | null
  sessionId: string
  inputId?: string | null
  turnId?: string | null
  agentId: string
  agentName: string
  status: string
  transcriptUrl: string
  jobUrl?: string | null
  observationUrl?: string | null
}

export interface AgentSessionLaunchContext {
  issueNumber?: number | null
  epicNumber?: number | null
  repository?: string | null
  workspacePath?: string | null
}

export interface AgentSessionLaunchInput {
  prompt: string
  context?: AgentSessionLaunchContext | null
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

export function getAgentLaunchObservationMeaning(observation: Pick<AgentLaunchObservationDto, 'turnStatus'>): AgentLaunchObservationMeaning {
  const status = observation.turnStatus.toLowerCase()
  if (status === 'unknown') return 'reconcile'
  if (status === 'completed' || status === 'failed') return 'result'
  return 'observe'
}

export interface GenericFollowupInput {
  text: string
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
  const search = runtimeSessionId
    ? `?${new URLSearchParams({ runtimeSessionId }).toString()}`
    : ''
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/transcript${search}`),
  )
}

export function launchAgentSession(projectId: string, agentRef: string, input: AgentSessionLaunchInput, idempotencyKey?: string) {
  return request<AgentSessionLaunchResponse>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/sessions`),
    {
      method: 'POST',
      body: JSON.stringify(input),
      headers: idempotencyKey ? { 'Idempotency-Key': idempotencyKey } : undefined,
    },
  )
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

export function postGenericFollowup(projectId: string, sessionId: string, input: GenericFollowupInput) {
  return request<{ status: string }>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/followup`),
    {
      method: 'POST',
      body: JSON.stringify(input),
    },
  )
}

export function cancelGenericSession(projectId: string, sessionId: string) {
  return request<{ state?: string }>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/cancel`),
    {
      method: 'POST',
    },
  )
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
      const hasIncomplete = data.turns.some(t => t.incomplete)
      return hasIncomplete ? 5000 : false
    },
  }
}

export function useGenericSessionTranscript(sessionId: string, runtimeSessionId?: string | null) {
  const { projectId } = useProject()
  return useQuery<AgentSessionTranscriptResponse>(genericSessionTranscriptQueryOptions(projectId, sessionId, runtimeSessionId))
}

/* ── Mutation hooks ─────────────────────────────────────── */

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

const CANCEL_TERMINAL_STATES = new Set(['completed', 'failed', 'stopped', 'cancelled'])

export function launchAgentSessionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ agentRef, prompt, context, idempotencyKey }: { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null; idempotencyKey?: string }) =>
      launchAgentSession(projectId!, agentRef, { prompt, context }, idempotencyKey),
    onSuccess: (_data: AgentSessionLaunchResponse, variables: { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null }) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
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

export function genericFollowupMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ sessionId, text }: { sessionId: string; text: string }) =>
      postGenericFollowup(projectId!, sessionId, { text }),
    onSuccess: (_data: { status: string }, variables: { sessionId: string; text: string; agentRef?: string }) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId, 'transcript'] })
      if (variables.agentRef) {
        queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      }
      toast.success('Follow-up sent')
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

export function cancelGenericSessionMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ sessionId }: { sessionId: string }) =>
      cancelGenericSession(projectId!, sessionId),
    onSuccess: (data: { state?: string }, variables: { sessionId: string; agentRef?: string }) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId] })
      if (variables.agentRef) {
        queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      }
      const state = data?.state
      if (state && CANCEL_TERMINAL_STATES.has(state)) {
        toast.success('Session cancelled')
        return
      }
      toast.warning('Session could not be cancelled')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useCancelGenericSession() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(cancelGenericSessionMutationOptions(projectId, queryClient))
}
