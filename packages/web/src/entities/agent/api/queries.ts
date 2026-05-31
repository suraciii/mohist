import { useQuery } from '@tanstack/react-query'
import type { AgentActivity, AgentSessionInfo } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { getAgentActivity, getAgentSessions, getAgentStatus } from './client'

export function useAgentStatus() {
  const { projectId } = useProject()
  return useQuery({
    queryKey: ['agent-status', projectId],
    queryFn: () => getAgentStatus(projectId),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}

export function useAgentSessions(params?: { status?: string; limit?: number }) {
  const { projectId } = useProject()
  return useQuery<AgentSessionInfo[]>({
    queryKey: ['agent-sessions', params, projectId],
    queryFn: () => getAgentSessions({ ...params, projectId }),
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
