import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  createAgentConnection,
  getConnectionDiagnostic,
  listAgentConnections,
} from './client'
import type {
  AgentConnectionCreateRequest,
  AgentConnectionCreateResponse,
  AgentConnectionDto,
} from '../model/types'

export function connectionDiagnosticQueryOptions(
  projectId: string | null | undefined,
  connectionId: string | null | undefined,
) {
  return {
    queryKey: ['agent-connection-diagnostic', projectId, connectionId],
    queryFn: () => getConnectionDiagnostic(projectId, connectionId!),
    enabled: !!projectId && !!connectionId,
  }
}

export function useConnectionDiagnostic(connectionId: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery(connectionDiagnosticQueryOptions(projectId, connectionId))
}

export const agentConnectionsQueryKey = (projectId: string | null | undefined) =>
  ['agent-connections', projectId] as const

export function agentConnectionsQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: agentConnectionsQueryKey(projectId),
    queryFn: () => listAgentConnections(projectId),
    enabled: !!projectId,
  }
}

export function useAgentConnections() {
  const { projectId } = useProject()
  return useQuery<AgentConnectionDto[]>(agentConnectionsQueryOptions(projectId))
}

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function createAgentConnectionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (data: AgentConnectionCreateRequest) => createAgentConnection(projectId, data),
    onSuccess: (_created: AgentConnectionCreateResponse) => {
      if (projectId) {
        queryClient.invalidateQueries({ queryKey: agentConnectionsQueryKey(projectId) })
      }
      toast.success('Slack Connection created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to create Slack Connection')
    },
  }
}

export function useCreateAgentConnection() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(createAgentConnectionMutationOptions(projectId, queryClient))
}
