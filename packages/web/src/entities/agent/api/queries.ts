import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentActivity, AgentSessionInfo } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import {
  archiveAgent,
  createAgent,
  getAgent,
  getAgentActivity,
  getAgentDetailStatus,
  getAgentListAvailability,
  getAgentSessions as getGlobalAgentSessions,
  getAgentStatus,
  listAgents,
  unarchiveAgent,
  updateAgent,
} from './client'
import type {
  AgentCreateRequest,
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentStatusDetailResponse,
  AgentUpdateRequest,
} from './client'
import { getAgentSessions as getAgentScopedSessions } from './agent-sessions'
import type { AgentSessionListItemDto } from './agent-sessions'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function useAgentStatus() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['agent-status', projectId],
    queryFn: () => getAgentStatus(projectId),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

export function useGlobalAgentSessions(params?: { status?: string; limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionInfo[]>({
    queryKey: ['agent-sessions', params, projectId],
    queryFn: () => getGlobalAgentSessions({ ...params, projectId }),
    enabled: !!projectId,
  })
}

export function useAgentActivity(params?: { limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentActivity>({
    queryKey: ['agent-activity', params, projectId],
    queryFn: () => getAgentActivity({ ...params, projectId }),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

/* ── Agent profile CRUD queries ─────────────────────────── */

export function agentsQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: ['agents', projectId],
    queryFn: () => listAgents(projectId!, { all: true }),
    enabled: !!projectId,
  }
}

export function useAgents() {
  const { projectId } = useProject()
  return useQuery<AgentInfo[]>(agentsQueryOptions(projectId))
}

export function agentListAvailabilityQueryKey(projectId: string | null | undefined) {
  return ['agent-availability', projectId] as const
}

export function agentListAvailabilityQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: agentListAvailabilityQueryKey(projectId),
    queryFn: () => getAgentListAvailability(projectId!),
    enabled: !!projectId,
    refetchInterval: 5000,
  }
}

export function useAgentListAvailability() {
  const { projectId } = useProject()
  return useQuery<AgentAvailabilitySummaryEntry[]>(agentListAvailabilityQueryOptions(projectId))
}

export function agentQueryOptions(projectId: string | null | undefined, agentRef: string) {
  return {
    queryKey: ['agents', projectId, agentRef],
    queryFn: () => getAgent(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  }
}

export function useAgent(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentInfo>(agentQueryOptions(projectId, agentRef))
}

export function createAgentMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (data: AgentCreateRequest) => createAgent(projectId!, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      toast.success('Agent created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useCreateAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(createAgentMutationOptions(projectId, queryClient))
}

export function updateAgentMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ agentRef, data }: { agentRef: string; data: AgentUpdateRequest }) =>
      updateAgent(projectId!, agentRef, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      toast.success('Agent updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useUpdateAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(updateAgentMutationOptions(projectId, queryClient))
}

export function archiveAgentMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (agentRef: string) => archiveAgent(projectId!, agentRef),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      toast.success('Agent archived')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useArchiveAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(archiveAgentMutationOptions(projectId, queryClient))
}

export function unarchiveAgentMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (agentRef: string) => unarchiveAgent(projectId!, agentRef),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      toast.success('Agent restored')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useUnarchiveAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(unarchiveAgentMutationOptions(projectId, queryClient))
}

/* ── Agent-scoped session list (consumes #130) ──────────── */

export function agentSessionsQueryOptions(projectId: string | null | undefined, agentRef: string) {
  return {
    queryKey: ['agents', projectId, agentRef, 'sessions'],
    queryFn: () => getAgentScopedSessions(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  }
}

export function useAgentSessions({ agentRef }: { agentRef: string }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionListItemDto[]>(agentSessionsQueryOptions(projectId, agentRef))
}

/* ── Per-agent server-side status (Readiness/Availability/waiting) ── */

export function agentDetailStatusQueryOptions(
  projectId: string | null | undefined,
  agentRef: string,
) {
  return {
    queryKey: ['agents', projectId, agentRef, 'status'],
    queryFn: () => getAgentDetailStatus(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
    refetchInterval: 5000,
  }
}

export function useAgentDetailStatus(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentStatusDetailResponse>(agentDetailStatusQueryOptions(projectId, agentRef))
}
