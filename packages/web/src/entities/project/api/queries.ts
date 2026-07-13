import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { useProject } from '../model/ProjectContext'
import { createProject, deleteProject, getProjects, getRepositories, addRepository, removeRepository, setDefaultRepository } from './client'
import { getProjectEvents, type ProjectEventDto } from './projectEvents'

export type ProjectCreator = typeof createProject

export function useProjects() {
  return useQuery({
    queryKey: ['projects'],
    queryFn: () => getProjects(),
  })
}

export function useRepositories(projectId: string | undefined) {
  return useQuery({
    queryKey: ['repositories', projectId],
    queryFn: () => getRepositories(projectId!),
    enabled: !!projectId,
  })
}

export function useAddRepository() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ projectId, data }: { projectId: string; data: { name: string; gitUrl: string; baseBranch?: string; isDefault?: boolean } }) =>
      addRepository(projectId, data),
    onSuccess: (_, { projectId }) => {
      queryClient.invalidateQueries({ queryKey: ['repositories', projectId] })
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Repository added')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useRemoveRepository() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ projectId, repoName }: { projectId: string; repoName: string }) =>
      removeRepository(projectId, repoName),
    onSuccess: (_, { projectId }) => {
      queryClient.invalidateQueries({ queryKey: ['repositories', projectId] })
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Repository removed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useSetDefaultRepository() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ projectId, repoName }: { projectId: string; repoName: string }) =>
      setDefaultRepository(projectId, repoName),
    onSuccess: (_, { projectId }) => {
      queryClient.invalidateQueries({ queryKey: ['repositories', projectId] })
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Default repository updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useCreateProject(projectCreator: ProjectCreator = createProject) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (data: { name: string }) => projectCreator(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Project created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useDeleteProject() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => deleteProject(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['projects'] })
      toast.success('Project deleted')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useProjectEvents(params?: { limit?: number }) {
  const { projectId } = useProject()
  return useQuery<ProjectEventDto[]>({
    queryKey: ['project-events', projectId],
    queryFn: () => getProjectEvents({ projectId, limit: params?.limit ?? 200 }),
    enabled: !!projectId,
    refetchInterval: 5000,
  })
}
