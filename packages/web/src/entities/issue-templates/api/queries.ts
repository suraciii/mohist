import { useQuery } from '@tanstack/react-query'
import { useProject } from '../../project/@x/project-context'
import { getIssueTemplate, getIssueTemplates } from './client'
import type { IssueTemplateDetail, IssueTemplateInfo } from '../model/types'

export function useIssueTemplates() {
  const { projectId } = useProject()
  return useQuery<IssueTemplateInfo[], Error>({
    queryKey: ['issue-templates', projectId],
    queryFn: () => getIssueTemplates(projectId),
    enabled: !!projectId,
  })
}

export function useIssueTemplate(name: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery<IssueTemplateDetail, Error>({
    queryKey: ['issue-template', projectId, name],
    queryFn: () => getIssueTemplate(name!, projectId),
    enabled: !!projectId && !!name,
  })
}
