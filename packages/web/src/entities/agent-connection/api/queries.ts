import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  claimAgentConnectionOwner,
  configureAgentConnection,
  createAgentConnection,
  getAgentConnection,
  getConnectionDiagnostic,
  listAgentConnections,
} from './client'
import type {
  AgentConnectionClaimOwnerResponse,
  AgentConnectionConfigureRequest,
  AgentConnectionCreateRequest,
  AgentConnectionCreateResponse,
  AgentConnectionDetailResponse,
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
    refetchInterval: 5000,
    refetchOnWindowFocus: true,
  }
}

export function useConnectionDiagnostic(connectionId: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery(connectionDiagnosticQueryOptions(projectId, connectionId))
}

export const agentConnectionDetailQueryKey = (
  projectId: string | null | undefined,
  connectionId: string | null | undefined,
) => ['agent-connection', projectId, connectionId] as const

export function agentConnectionDetailQueryOptions(
  projectId: string | null | undefined,
  connectionId: string | null | undefined,
) {
  return {
    queryKey: agentConnectionDetailQueryKey(projectId, connectionId),
    queryFn: () => getAgentConnection(projectId, connectionId!),
    enabled: !!projectId && !!connectionId,
    refetchInterval: 5000,
    refetchOnWindowFocus: true,
  }
}

export function useAgentConnection(connectionId: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery<AgentConnectionDetailResponse>(
    agentConnectionDetailQueryOptions(projectId, connectionId),
  )
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

function invalidateAgentConnectionQueries(
  queryClient: InvalidationClient,
  projectId: string | null | undefined,
  connectionId?: string,
) {
  if (!projectId) return
  queryClient.invalidateQueries({ queryKey: agentConnectionsQueryKey(projectId) })
  queryClient.invalidateQueries({
    queryKey: ['agent-connection-diagnostic', projectId, connectionId],
  })
  queryClient.invalidateQueries({
    queryKey: ['agent-connection', projectId, connectionId],
  })
}

export function createAgentConnectionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (data: AgentConnectionCreateRequest) => createAgentConnection(projectId, data),
    onSuccess: (_created: AgentConnectionCreateResponse) => {
      invalidateAgentConnectionQueries(queryClient, projectId)
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

export function configureAgentConnectionMutationOptions(
  projectId: string | null | undefined,
  connectionId: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (data: AgentConnectionConfigureRequest) =>
      configureAgentConnection(projectId, connectionId, data),
    onSuccess: (_updated: AgentConnectionDto) => {
      invalidateAgentConnectionQueries(queryClient, projectId, connectionId)
      toast.success('Credentials saved')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to save credentials')
    },
  }
}

export function useConfigureAgentConnection(connectionId: string | null | undefined) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(
    configureAgentConnectionMutationOptions(projectId, connectionId ?? '', queryClient),
  )
}

export function claimAgentConnectionOwnerMutationOptions(
  projectId: string | null | undefined,
  connectionId: string,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: () => claimAgentConnectionOwner(projectId, connectionId),
    onSuccess: (_response: AgentConnectionClaimOwnerResponse) => {
      invalidateAgentConnectionQueries(queryClient, projectId, connectionId)
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to generate owner claim code')
    },
  }
}

export function useClaimAgentConnectionOwner(connectionId: string | null | undefined) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(
    claimAgentConnectionOwnerMutationOptions(projectId, connectionId ?? '', queryClient),
  )
}
