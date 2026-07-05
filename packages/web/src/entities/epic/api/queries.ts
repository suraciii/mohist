import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { Epic, EpicDetail, EpicWithProgress, StoredCloudEventDto } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { startIssue } from '../../issue'
import { addEpicIssue, batchAddEpicIssues, batchRemoveEpicIssues, closeEpic, createEpic, getEpic, getEpicEvents, getEpics, markEpicDone, pauseEpic, removeEpicIssue, reopenEpic, resumeEpic, startEpic, updateEpic, type BatchMembershipResponse, type UpdateEpicInput } from './client'

export interface UseEpicsParams {
  search?: string
  sort?: string
  dir?: string
}

export function useEpics(params: UseEpicsParams = {}) {
  const { projectId } = useProject()
  const { search, sort, dir } = params
  return useQuery<EpicWithProgress[]>({
    queryKey: ['epics', projectId, { search: search ?? null, sort: sort ?? null, dir: dir ?? null }],
    queryFn: () => getEpics({ projectId: projectId ?? undefined, search, sort, dir }),
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

export function useBatchAddEpicIssues() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<BatchMembershipResponse, Error, { epicId: string; issueIds: string[] }>({
    mutationFn: ({ epicId, issueIds }) => batchAddEpicIssues(epicId, issueIds, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issues added to Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useBatchRemoveEpicIssues() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<BatchMembershipResponse, Error, { epicId: string; issueIds: string[] }>({
    mutationFn: ({ epicId, issueIds }) => batchRemoveEpicIssues(epicId, issueIds, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', variables.epicId] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Issues removed from Epic')
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

export function useStartEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => startEpic(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, id] })
      toast.success('Epic started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useReopenEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, string>({
    mutationFn: (id) => reopenEpic(id, projectId),
    onSuccess: (_data, id) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, id] })
      queryClient.invalidateQueries({ queryKey: ['issues'] })
      toast.success('Epic reopened')
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

export function useEpicEvents(id: string | null | undefined, enabled: boolean = true) {
  const { projectId } = useProject()
  const safeId = typeof id === 'string' && id.length > 0 ? id : null
  return useQuery<StoredCloudEventDto[]>({
    queryKey: ['epics', projectId, safeId, 'events'],
    queryFn: () => getEpicEvents(safeId!, projectId),
    enabled: enabled && !!projectId && !!safeId,
  })
}
