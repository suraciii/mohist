import { request, projectApiPath } from '../../../shared/api/client'

export interface ProjectEventDto {
  id: number
  origin: string
  sourceAggregateKind: string
  sourceAggregateId: string
  source: string
  type: string
  time: string
  envelopeId: string
  specVersion: string
  subject: string | null
  dataContentType: string | null
  data: Record<string, unknown>
  extensions: Record<string, string>
  runnerId: string | null
}

export function getProjectEvents(params?: { projectId?: string | null; limit?: number }) {
  const search = new URLSearchParams()
  if (params?.limit != null) search.set('limit', String(params.limit))
  const qs = search.toString()
  return request<ProjectEventDto[]>(projectApiPath(params?.projectId, `/events${qs ? `?${qs}` : ''}`))
}
