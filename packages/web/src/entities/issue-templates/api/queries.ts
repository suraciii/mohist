import { useQuery } from '@tanstack/react-query'
import { useProject } from '../../project/@x/project-context'
import { getIssueTemplate, getIssueTemplates } from './client'

export function issueTemplatesQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: ['issue-templates', projectId] as const,
    queryFn: () => getIssueTemplates(projectId),
    enabled: !!projectId,
  } as const
}

export function issueTemplateQueryOptions(projectId: string | null | undefined, name: string | null | undefined) {
  return {
    queryKey: ['issue-template', projectId, name] as const,
    queryFn: () => getIssueTemplate(name!, projectId),
    enabled: !!projectId && !!name,
  } as const
}

export function useIssueTemplates() {
  const { projectId } = useProject()
  return useQuery(issueTemplatesQueryOptions(projectId))
}

export function useIssueTemplate(name: string | null | undefined) {
  const { projectId } = useProject()
  return useQuery(issueTemplateQueryOptions(projectId, name))
}
