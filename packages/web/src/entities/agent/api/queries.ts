import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentActivity, AgentSessionInfo } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import {
  archiveAgent,
  createAgent,
  customizeAgent,
  getAgent,
  getAgentActivity,
  getAgentByName,
  getAgentDetailStatus,
  getAgentListAvailability,
  getAgentSessions as getGlobalAgentSessions,
  getAgentStatus,
  isBuiltInAgentRef,
  listAgents,
  unarchiveAgent,
  updateAgent,
  BUILT_IN_AGENT_ID_PREFIX,
} from './client'
import type {
  AgentCreateRequest,
  AgentAvailabilitySummaryEntry,
  AgentInfo,
  AgentOverrideRequest,
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
    // Built-in definitions are not stored rows: their ref is `builtin:<name>`
    // and only the by-name route resolves them.
    queryFn: () =>
      isBuiltInAgentRef(agentRef)
        ? getAgentByName(projectId!, agentRef.slice(BUILT_IN_AGENT_ID_PREFIX.length))
        : getAgent(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  }
}

export function useAgent(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentInfo>(agentQueryOptions(projectId, agentRef))
}

export function agentByNameQueryOptions(projectId: string | null | undefined, name: string | null | undefined) {
  return {
    queryKey: ['agents', projectId, 'by-name', name],
    queryFn: () => getAgentByName(projectId!, name!),
    enabled: !!projectId && !!name,
  }
}

/** Resolves the Project's effective Agent for a name, including built-ins. */
export function useAgentByName(name: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery<AgentInfo>(agentByNameQueryOptions(projectId, name))
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

export function customizeBuiltInAgentMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (data: AgentOverrideRequest) => customizeAgent(projectId!, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

/**
 * Materializes a built-in Workflow Agent as a same-name Project Agent. The
 * caller reacts to the named 409 repair cases from `ApiError.code`.
 */
export function useCustomizeBuiltInAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(customizeBuiltInAgentMutationOptions(projectId, queryClient))
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

export function agentSessionsQueryOptions(projectId: string | null | undefined, agentRef: string, enabled = true) {
  return {
    queryKey: ['agents', projectId, agentRef, 'sessions'],
    queryFn: () => getAgentScopedSessions(projectId!, agentRef),
    enabled: !!projectId && !!agentRef && enabled,
  }
}

export function useAgentSessions({ agentRef, enabled = true }: { agentRef: string; enabled?: boolean }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionListItemDto[]>(agentSessionsQueryOptions(projectId, agentRef, enabled))
}

/* ── Per-agent server-side status (Executability/Availability/waiting) ── */

export function agentDetailStatusQueryOptions(projectId: string | null | undefined, agentRef: string, enabled = true) {
  return {
    queryKey: ['agents', projectId, agentRef, 'status'],
    queryFn: () => getAgentDetailStatus(projectId!, agentRef),
    enabled: !!projectId && !!agentRef && enabled,
    refetchInterval: 5000,
  }
}

export function useAgentDetailStatus(agentRef: string, enabled = true) {
  const { projectId } = useProject()
  return useQuery<AgentStatusDetailResponse>(agentDetailStatusQueryOptions(projectId, agentRef, enabled))
}
