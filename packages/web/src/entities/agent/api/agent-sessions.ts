import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentSessionTranscriptResponse, AgentSessionUsage } from '../../coder-session/model/types'
import { useProject } from '../../project/@x/project-context'
import { projectApiPath, request } from '../../../shared/api/client'

/* ── DTO types (matching #129/#130 server contracts) ────── */

export interface AgentSessionListContextRefsDto {
  issueNumber?: number | null
  epicNumber?: string | null
  repository?: string | null
  workspacePath?: string | null
}

export interface AgentSessionListItemDto {
  sessionId: string
  agentId: string
  agentName: string
  status: string
  createdAt: string
  lastActivityAt: string | null
  resolvedModel: string | null
  contextRefs?: AgentSessionListContextRefsDto | null
}

export interface GenericAgentSessionSummaryDto {
  sessionId: string
  agentId: string
  agentName: string
  status: string
  createdAt: string
  lastActivityAt: string | null
  resolvedModel: string | null
  failureCategory: string | null
  toolCallCount: number | null
  toolErrorCount: number | null
  contextRefs: AgentSessionListContextRefsDto | null
  usage: AgentSessionUsage
}

export interface AgentSessionLaunchResponse {
  sessionId: string
  agentId: string
  agentName: string
  status: string
  transcriptUrl: string
}

export interface AgentSessionLaunchContext {
  issueNumber?: number | null
  epicNumber?: string | null
  repository?: string | null
  workspacePath?: string | null
}

export interface AgentSessionLaunchInput {
  prompt: string
  context?: AgentSessionLaunchContext | null
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

export function getGenericSessionTranscript(projectId: string, sessionId: string) {
  return request<AgentSessionTranscriptResponse>(
    projectApiPath(projectId, `/agent-sessions/${encodeURIComponent(sessionId)}/transcript`),
  )
}

export function launchAgentSession(projectId: string, agentRef: string, input: AgentSessionLaunchInput) {
  return request<AgentSessionLaunchResponse>(
    projectApiPath(projectId, `/agents/${encodeURIComponent(agentRef)}/sessions`),
    {
      method: 'POST',
      body: JSON.stringify(input),
    },
  )
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

export function useGenericSessionSummary(sessionId: string) {
  const { projectId } = useProject()
  return useQuery<GenericAgentSessionSummaryDto>({
    queryKey: ['agent-session', projectId, sessionId],
    queryFn: () => getGenericSessionSummary(projectId!, sessionId),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query) => {
      const data = query.state.data as GenericAgentSessionSummaryDto | undefined
      if (!data) return 5000
      const terminal = data.status === 'completed' || data.status === 'failed' || data.status === 'stopped'
      return terminal ? false : 5000
    },
  })
}

export function useGenericSessionTranscript(sessionId: string) {
  const { projectId } = useProject()
  return useQuery<AgentSessionTranscriptResponse>({
    queryKey: ['agent-session', projectId, sessionId, 'transcript'],
    queryFn: () => getGenericSessionTranscript(projectId!, sessionId),
    enabled: !!projectId && !!sessionId,
    refetchInterval: (query) => {
      const data = query.state.data as AgentSessionTranscriptResponse | undefined
      if (!data || !data.turns) return 5000
      const hasIncomplete = data.turns.some(t => t.incomplete)
      return hasIncomplete ? 5000 : false
    },
  })
}

/* ── Mutation hooks ─────────────────────────────────────── */

export function useLaunchAgentSession() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentSessionLaunchResponse, Error, { agentRef: string; prompt: string; context?: AgentSessionLaunchContext | null }>({
    mutationFn: ({ agentRef, prompt, context }) =>
      launchAgentSession(projectId!, agentRef, { prompt, context }),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      toast.success('Session launched')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useGenericFollowup() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ status: string }, Error, { sessionId: string; text: string; agentRef?: string }>({
    mutationFn: ({ sessionId, text }) =>
      postGenericFollowup(projectId!, sessionId, { text }),
    onSuccess: (_data, variables) => {
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
  })
}

export function useCancelGenericSession() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ state?: string }, Error, { sessionId: string; agentRef?: string }>({
    mutationFn: ({ sessionId }) =>
      cancelGenericSession(projectId!, sessionId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      queryClient.invalidateQueries({ queryKey: ['agent-activity'] })
      queryClient.invalidateQueries({ queryKey: ['agent-session', projectId, variables.sessionId] })
      if (variables.agentRef) {
        queryClient.invalidateQueries({ queryKey: ['agents', projectId, variables.agentRef, 'sessions'] })
      }
      toast.success('Session cancelled')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}
