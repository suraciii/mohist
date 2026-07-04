import { request } from '../../../shared/api/client'
import type { IssueTemplateDetail, IssueTemplateInfo } from '../model/types'

export function getIssueTemplates(projectId: string | null | undefined) {
  return request<IssueTemplateInfo[]>(issueTemplatesPath(projectId))
}

export function getIssueTemplate(name: string, projectId: string | null | undefined) {
  return request<IssueTemplateDetail>(`/issue-templates/${name}${projectQuery(projectId)}`)
}

function issueTemplatesPath(projectId: string | null | undefined) {
  return `/issue-templates${projectQuery(projectId)}`
}

function projectQuery(projectId: string | null | undefined) {
  if (!projectId) throw new Error('Project is required')
  return `?projectId=${encodeURIComponent(projectId)}`
}
