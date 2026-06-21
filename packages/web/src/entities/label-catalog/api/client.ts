import { ApiError, projectApiPath, request } from '../../../shared/api/client'
import type { ApiResponse } from '../../../shared/api/types'
import type { LabelDefinition, LabelDefinitionInput, LabelDefinitionPatch } from '../model/types'

export const LABEL_KEY_PATTERN = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/

export function isValidLabelKey(key: string): boolean {
  return LABEL_KEY_PATTERN.test(key)
}

export function getLabelCatalog(projectId: string | null | undefined) {
  return request<LabelDefinition[]>(projectApiPath(projectId, '/labels/catalog'))
}

export function createLabelDefinition(
  projectId: string,
  input: LabelDefinitionInput,
) {
  const body: Record<string, unknown> = {
    key: input.key,
    description: input.description,
  }
  if (input.supportedValues !== undefined && input.supportedValues !== null) {
    body.supportedValues = input.supportedValues
  }
  return request<LabelDefinition>(projectApiPath(projectId, '/labels/catalog'), {
    method: 'POST',
    body: JSON.stringify(body),
  })
}

export function updateLabelDefinition(
  projectId: string,
  key: string,
  patch: LabelDefinitionPatch,
) {
  const body: Record<string, unknown> = {}
  if (patch.description !== undefined) body.description = patch.description
  if (patch.supportedValues !== undefined) body.supportedValues = patch.supportedValues

  return request<LabelDefinition>(
    projectApiPath(projectId, `/labels/catalog/${encodeURIComponent(key)}`),
    {
      method: 'PATCH',
      body: JSON.stringify(body),
    },
  )
}

export async function deleteLabelDefinition(projectId: string, key: string): Promise<void> {
  const path = projectApiPath(projectId, `/labels/catalog/${encodeURIComponent(key)}`)
  const res = await fetch(`/api${path}`, {
    method: 'DELETE',
    headers: { 'Content-Type': 'application/json' },
  })

  if (res.status === 204) {
    return
  }

  const text = await res.text()
  let json: ApiResponse<unknown> | null = null
  if (text.trim()) {
    try {
      json = JSON.parse(text) as ApiResponse<unknown>
    } catch {
      throw new ApiError(`Invalid JSON response from ${path}`, res.status)
    }
  }

  if (json && json.success === false) {
    throw new ApiError(
      json.error ?? `Request failed: ${res.status}`,
      res.status,
      json.data,
      json.code,
      json.details,
    )
  }

  if (!res.ok) {
    throw new ApiError(`Request failed: ${res.status}`, res.status)
  }
}
