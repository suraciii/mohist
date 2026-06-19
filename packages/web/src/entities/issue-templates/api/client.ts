import { request } from '../../../shared/api/client'
import type { IssueTemplateDetail, IssueTemplateInfo } from '../model/types'

export function getIssueTemplates(projectId: string | null | undefined) {
  return request<IssueTemplateInfo[]>(issueTemplatesPath(projectId))
}

export function getIssueTemplate(name: string, projectId: string | null | undefined) {
  return request<IssueTemplateDetail>(`/issue-templates/${name}${projectQuery(projectId)}`)
}

export function composeIssueTemplateBody(template: Pick<IssueTemplateDetail, 'sections'>): string {
  return template.sections
    .map((section) => `## ${section.title}\n${section.placeholder}`)
    .join('\n\n')
}

function issueTemplatesPath(projectId: string | null | undefined) {
  return `/issue-templates${projectQuery(projectId)}`
}

function projectQuery(projectId: string | null | undefined) {
  if (!projectId) throw new Error('Project is required')
  return `?projectId=${encodeURIComponent(projectId)}`
}
