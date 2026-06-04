import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import {
  deleteProjectTemplateOverride,
  extractVariables,
  getProjectTemplateOverride,
  getProjectTemplates,
  getSystemTemplates,
  previewProjectTemplate,
  upsertProjectTemplateOverride,
} from './client'
import type {
  PreviewResponse,
  ProjectTemplateOverride,
  ProjectTemplateOverridePayload,
} from '../model/types'

export function useSystemTemplates() {
  return useQuery({
    queryKey: ['system-templates'],
    queryFn: () => getSystemTemplates(),
  })
}

export function useProjectTemplates(projectId: string | undefined) {
  return useQuery({
    queryKey: ['project-templates', projectId],
    queryFn: () => getProjectTemplates(projectId!),
    enabled: !!projectId,
  })
}

export function useProjectTemplateOverride(projectId: string | undefined, key: string | undefined) {
  return useQuery<ProjectTemplateOverride>({
    queryKey: ['project-template', projectId, key, 'override'],
    queryFn: () => getProjectTemplateOverride(projectId!, key!),
    enabled: !!projectId && !!key,
    retry: (failureCount, error) => {
      const status = (error as { status?: number } | null)?.status
      if (status === 404) return false
      return failureCount < 1
    },
  })
}

interface UpsertOverrideInput {
  key: string
  payload: ProjectTemplateOverridePayload
}

export function useUpsertProjectTemplateOverride(projectId: string | undefined) {
  const queryClient = useQueryClient()
  return useMutation<ProjectTemplateOverride, Error, UpsertOverrideInput>({
    mutationFn: ({ key, payload }) => upsertProjectTemplateOverride(projectId!, key, payload),
    onSuccess: (_data, { key }) => {
      queryClient.invalidateQueries({ queryKey: ['project-templates', projectId] })
      queryClient.invalidateQueries({ queryKey: ['project-template', projectId, key] })
      queryClient.invalidateQueries({ queryKey: ['project-template', projectId, key, 'override'] })
      toast.success('Template saved')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useDeleteProjectTemplateOverride(projectId: string | undefined) {
  const queryClient = useQueryClient()
  return useMutation<{ message: string }, Error, { key: string }>({
    mutationFn: ({ key }) => deleteProjectTemplateOverride(projectId!, key),
    onSuccess: (_data, { key }) => {
      queryClient.invalidateQueries({ queryKey: ['project-templates', projectId] })
      queryClient.invalidateQueries({ queryKey: ['project-template', projectId, key] })
      queryClient.invalidateQueries({ queryKey: ['project-template', projectId, key, 'override'] })
      toast.success('Template reset')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

interface PreviewInput {
  variables: Record<string, unknown>
}

export function usePreviewProjectTemplate(projectId: string | undefined, key: string | undefined) {
  return useMutation<PreviewResponse, Error, PreviewInput>({
    mutationFn: ({ variables }) => previewProjectTemplate(projectId!, key!, variables),
  })
}

export function useExtractVariables() {
  return useMutation<{ variables: string[] }, Error, { body: string }>({
    mutationFn: ({ body }) => extractVariables(body),
  })
}
