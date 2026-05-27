import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '../../../shared/api/client'
import type { Epic, EpicDetail, EpicWithProgress } from '../../../shared/api/types'
import { useProject } from '../../project/@x/project-context'

export function useEpics() {
  const { projectId } = useProject()
  return useQuery<EpicWithProgress[]>({
    queryKey: ['epics', projectId],
    queryFn: () => api.getEpics({ projectId: projectId ?? undefined }),
    enabled: !!projectId,
  })
}

export function useEpic(id: string) {
  const { projectId } = useProject()
  return useQuery<EpicDetail>({
    queryKey: ['epics', projectId, id],
    queryFn: () => api.getEpic(id, { projectId: projectId ?? undefined }),
    enabled: !!projectId && !!id,
  })
}

export function useCreateEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { title: string; description: string; priority: string }>({
    mutationFn: (data) => api.createEpic({ ...data, projectId: projectId ?? undefined }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      toast.success('Epic created')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAddEpicIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ epicId: string; issueId: string }, Error, { epicId: string; issueId: string }>({
    mutationFn: ({ epicId, issueId }) => api.addEpicIssue(epicId, issueId, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issue added to Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useRemoveEpicIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ epicId: string; issueId: string }, Error, { epicId: string; issueId: string }>({
    mutationFn: ({ epicId, issueId }) => api.removeEpicIssue(epicId, issueId, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issue removed from Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useMarkEpicDone() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => api.markEpicDone(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', id] })
      toast.success('Epic marked as done')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useCloseEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => api.closeEpic(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', id] })
      toast.success('Epic closed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}
