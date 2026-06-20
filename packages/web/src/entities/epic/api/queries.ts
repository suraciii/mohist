import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { Epic, EpicDetail, EpicWithProgress } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { startIssue } from '../../issue'
import { addEpicIssue, closeEpic, createEpic, getEpic, getEpics, markEpicDone, pauseEpic, removeEpicIssue, resumeEpic, updateEpic, type UpdateEpicInput } from './client'

export function useEpics() {
  const { projectId } = useProject()
  return useQuery<EpicWithProgress[]>({
    queryKey: ['epics', projectId],
    queryFn: () => getEpics({ projectId: projectId ?? undefined }),
    enabled: !!projectId,
  })
}

export function useEpic(id: string) {
  const { projectId } = useProject()
  return useQuery<EpicDetail>({
    queryKey: ['epics', projectId, id],
    queryFn: () => getEpic(id, { projectId: projectId ?? undefined }),
    enabled: !!projectId && !!id,
  })
}

export function useCreateEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { title: string; description: string; priority: string }>({
    mutationFn: (data) => createEpic({ ...data, projectId: projectId ?? undefined }),
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
    mutationFn: ({ epicId, issueId }) => addEpicIssue(epicId, issueId, projectId),
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
    mutationFn: ({ epicId, issueId }) => removeEpicIssue(epicId, issueId, projectId),
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

export function useStartIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Awaited<ReturnType<typeof startIssue>>, Error, number>({
    mutationFn: (number) => startIssue(number, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issue started')
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
    mutationFn: (id) => markEpicDone(id, projectId),
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
    mutationFn: (id) => closeEpic(id, projectId),
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

export function usePauseEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { id: string; reason?: string | null }>({
    mutationFn: ({ id, reason }) => pauseEpic(id, reason, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.id] })
      toast.success('Epic paused')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useResumeEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => resumeEpic(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, id] })
      toast.success('Epic resumed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useUpdateEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { id: string; data: UpdateEpicInput }>({
    mutationFn: ({ id, data }) => updateEpic(id, data, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.id] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Epic updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}
