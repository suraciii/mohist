import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentActivity, AgentSessionInfo } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import {
  archiveAgent,
  createAgent,
  getAgent,
  getAgentActivity,
  getAgentSessions as getGlobalAgentSessions,
  getAgentStatus,
  listAgents,
  unarchiveAgent,
  updateAgent,
} from './client'
import type {
  AgentCreateRequest,
  AgentInfo,
  AgentUpdateRequest,
} from './client'
import { getAgentSessions as getAgentScopedSessions } from './agent-sessions'
import type { AgentSessionListItemDto } from './agent-sessions'

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

export function useAgents() {
  const { projectId } = useProject()
  return useQuery<AgentInfo[]>({
    queryKey: ['agents', projectId],
    queryFn: () => listAgents(projectId!, { all: true }),
    enabled: !!projectId,
  })
}

export function useAgent(agentRef: string) {
  const { projectId } = useProject()
  return useQuery<AgentInfo>({
    queryKey: ['agents', projectId, agentRef],
    queryFn: () => getAgent(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  })
}

export function useCreateAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentInfo, Error, AgentCreateRequest>({
    mutationFn: (data) => createAgent(projectId!, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      toast.success('Agent created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useUpdateAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentInfo, Error, { agentRef: string; data: AgentUpdateRequest }>({
    mutationFn: ({ agentRef, data }) => updateAgent(projectId!, agentRef, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      toast.success('Agent updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useArchiveAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentInfo, Error, string>({
    mutationFn: (agentRef) => archiveAgent(projectId!, agentRef),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      toast.success('Agent archived')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useUnarchiveAgent() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<AgentInfo, Error, string>({
    mutationFn: (agentRef) => unarchiveAgent(projectId!, agentRef),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agents'] })
      queryClient.invalidateQueries({ queryKey: ['agent-status'] })
      toast.success('Agent restored')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

/* ── Agent-scoped session list (consumes #130) ──────────── */

export function useAgentSessions({ agentRef }: { agentRef: string }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionListItemDto[]>({
    queryKey: ['agents', projectId, agentRef, 'sessions'],
    queryFn: () => getAgentScopedSessions(projectId!, agentRef),
    enabled: !!projectId && !!agentRef,
  })
}
