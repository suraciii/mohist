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
  data: JsonValue
  extensions: Record<string, string>
  runnerId: string | null
  issueNumber?: number | null
  sessionSourceKind?: string | null
  workflowRunId?: string | null
  agentId?: string | null
  agentName?: string | null
}

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue }
export type ProjectEventTypeFilter = 'issue-state' | 'workflow-stage' | 'agent-session' | 'runner' | 'failure'

export function getProjectEvents(params?: {
  projectId?: string | null
  limit?: number
  types?: readonly ProjectEventTypeFilter[]
  attentionOnly?: boolean
}) {
  const search = new URLSearchParams()
  if (params?.limit != null) search.set('limit', String(params.limit))
  if (params?.types?.length) search.set('types', params.types.join(','))
  if (params?.attentionOnly) search.set('attentionOnly', 'true')
  const qs = search.toString()
  return request<ProjectEventDto[]>(projectApiPath(params?.projectId, `/events${qs ? `?${qs}` : ''}`))
}
