import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../../project/@x/project-context'
import {
  createLabelDefinition,
  deleteLabelDefinition,
  getLabelCatalog,
  updateLabelDefinition,
} from './client'
import type { LabelDefinition, LabelDefinitionInput, LabelDefinitionPatch } from '../model/types'

const catalogQueryKey = (projectId: string | null | undefined) =>
  ['label-catalog', projectId] as const

export function useLabelCatalog() {
  const { projectId } = useProject()
  return useQuery<LabelDefinition[], Error>({
    queryKey: catalogQueryKey(projectId),
    queryFn: () => getLabelCatalog(projectId),
    enabled: !!projectId,
  })
}

export function useCreateLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<LabelDefinition, Error, LabelDefinitionInput>({
    mutationFn: (input) => {
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
  })
}

export function useUpdateLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<LabelDefinition, Error, { key: string; patch: LabelDefinitionPatch }>({
    mutationFn: ({ key, patch }) => {
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
  })
}

export function useDeleteLabelDefinition() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<void, Error, string>({
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
  })
}

