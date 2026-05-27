import { useQuery } from '@tanstack/react-query'
import { api } from '../../../shared/api/client'
import type { AgentActivity, AgentSessionInfo } from '../../../shared/api/types'
import { useProject } from '../../project/@x/project-context'

export function useAgentStatus() {
  return useQuery({
    queryKey: ['agent-status'],
    queryFn: () => api.getAgentStatus(),
    refetchInterval: 5000,
  })
}

export function useAgentSessions(params?: { status?: string; limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionInfo[]>({
    queryKey: ['agent-sessions', params, projectId],
    queryFn: () => api.getAgentSessions({ ...params, projectId }),
    enabled: !!projectId,
  })
}

export function useAgentActivity(params?: { limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentActivity>({
    queryKey: ['agent-activity', params, projectId],
    queryFn: () => api.getAgentActivity({ ...params, projectId }),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}
