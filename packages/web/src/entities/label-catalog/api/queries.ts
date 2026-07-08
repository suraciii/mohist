import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  createLabelDefinition,
  deleteLabelDefinition,
  getLabelCatalog,
  updateLabelDefinition,
} from './client'
import type { LabelDefinition, LabelDefinitionInput, LabelDefinitionPatch } from '../model/types'

export const catalogQueryKey = (projectId: string | null | undefined) =>
  ['label-catalog', projectId] as const

export function labelCatalogQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: catalogQueryKey(projectId),
    queryFn: () => getLabelCatalog(projectId),
    enabled: !!projectId,
  } as const
}

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

export function createLabelDefinitionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (input: LabelDefinitionInput) => {
      if (!projectId) throw new Error('Project is required')
      return createLabelDefinition(projectId, input)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: catalogQueryKey(projectId) })
      toast.success('Label definition added')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to add label definition')
    },
  }
}

export function updateLabelDefinitionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: ({ key, patch }: { key: string; patch: LabelDefinitionPatch }) => {
      if (!projectId) throw new Error('Project is required')
      return updateLabelDefinition(projectId, key, patch)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: catalogQueryKey(projectId) })
      toast.success('Label definition updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to update label definition')
    },
  }
}

export function deleteLabelDefinitionMutationOptions(
  projectId: string | null | undefined,
  queryClient: InvalidationClient,
) {
  return {
    mutationFn: (key: string) => {
      if (!projectId) throw new Error('Project is required')
      return deleteLabelDefinition(projectId, key)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: catalogQueryKey(projectId) })
      toast.success('Label definition removed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to remove label definition')
    },
  }
}

export function useLabelCatalog() {
  const { projectId } = useProject()
  return useQuery<LabelDefinition[], Error>(labelCatalogQueryOptions(projectId))
}

export function useCreateLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(createLabelDefinitionMutationOptions(projectId, queryClient))
}

export function useUpdateLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(updateLabelDefinitionMutationOptions(projectId, queryClient))
}

export function useDeleteLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(deleteLabelDefinitionMutationOptions(projectId, queryClient))
}

