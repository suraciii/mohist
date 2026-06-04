import { request } from '../../../shared/api/client'
import type {
  ExtractVariablesResponse,
  PreviewResponse,
  ProjectTemplate,
  ProjectTemplateOverride,
  ProjectTemplateOverridePayload,
  SystemTemplate,
} from '../model/types'

export function getSystemTemplates() {
  return request<SystemTemplate[]>('/templates/system')
}

export function getProjectTemplates(projectId: string) {
  return request<ProjectTemplate[]>(`/projects/${encodeURIComponent(projectId)}/templates`)
}

export function getProjectTemplate(projectId: string, key: string) {
  return request<ProjectTemplate>(
    `/projects/${encodeURIComponent(projectId)}/templates/${encodeURIComponent(key)}`,
  )
}

export function getProjectTemplateOverride(projectId: string, key: string) {
  return request<ProjectTemplateOverride>(
    `/projects/${encodeURIComponent(projectId)}/templates/${encodeURIComponent(key)}/override`,
  )
}

export function upsertProjectTemplateOverride(
  projectId: string,
  key: string,
  payload: ProjectTemplateOverridePayload,
) {
  return request<ProjectTemplateOverride>(
    `/projects/${encodeURIComponent(projectId)}/templates/${encodeURIComponent(key)}/override`,
    {
      method: 'PUT',
      body: JSON.stringify(payload),
    },
  )
}

export function deleteProjectTemplateOverride(projectId: string, key: string) {
  return request<{ message: string }>(
    `/projects/${encodeURIComponent(projectId)}/templates/${encodeURIComponent(key)}/override`,
    {
      method: 'DELETE',
    },
  )
}

export function previewProjectTemplate(
  projectId: string,
  key: string,
  variables: Record<string, unknown>,
) {
  return request<PreviewResponse>(
    `/projects/${encodeURIComponent(projectId)}/templates/${encodeURIComponent(key)}/preview`,
    {
      method: 'POST',
      body: JSON.stringify({ variables }),
    },
  )
}

export function extractVariables(body: string) {
  return request<ExtractVariablesResponse>('/templates/extract-variables', {
    method: 'POST',
    body: JSON.stringify({ body }),
  })
}
