import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { QueryClient } from '@tanstack/react-query'
import type { Workspace } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { closeWorkspace, getWorkspace, getWorkspaces } from './client'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function useWorkspaces(status?: string) {
  const { projectId } = useProject()
  return useQuery<Workspace[]>({
    queryKey: ['workspaces', projectId, { status: status ?? null }],
    queryFn: () => getWorkspaces({ projectId: projectId ?? undefined, status }),
    enabled: !!projectId,
  })
}

export function useWorkspace(name: string | null) {
  const { projectId } = useProject()
  return useQuery<Workspace>({
    queryKey: ['workspaces', projectId, name],
    queryFn: () => getWorkspace(name!, { projectId: projectId ?? undefined }),
    enabled: !!projectId && name !== null,
  })
}

export function closeWorkspaceMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (name: string) => closeWorkspace(name, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workspaces'] })
      toast.success('Workspace archived')
    },
  }
}

export function useCloseWorkspace() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Workspace, Error, string>(closeWorkspaceMutationOptions(projectId, queryClient))
}
