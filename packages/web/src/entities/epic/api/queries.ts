import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { Epic, EpicDetail, EpicWithProgress, StoredCloudEventDto } from '../model/types'
import { useProject } from '../../project/@x/project-context'
import { issueListKeys } from '../../issue/@x/query-keys'
import { startIssue } from '../../issue/@x/actions'
import { addEpicIssue, batchAddEpicIssues, batchRemoveEpicIssues, closeEpic, createEpic, getEpic, getEpicEvents, getEpics, markEpicDone, pauseEpic, removeEpicIssue, reopenEpic, resumeEpic, startEpic, updateEpic, type BatchMembershipResponse, type UpdateEpicInput } from './client'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

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

export function useEpic(number: number | null) {
  const { projectId } = useProject()
  return useQuery<EpicDetail>({
    queryKey: ['epics', projectId, number],
    queryFn: () => getEpic(number!, { projectId: projectId ?? undefined }),
    enabled: !!projectId && number !== null,
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
  return useMutation<{ epicNumber: number; issueNumber: number }, Error, { epicNumber: number; issueNumber: number }>({
    mutationFn: ({ epicNumber, issueNumber }) => addEpicIssue(epicNumber, issueNumber, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.epicNumber] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
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
  return useMutation<{ epicNumber: number; issueNumber: number }, Error, { epicNumber: number; issueNumber: number }>({
    mutationFn: ({ epicNumber, issueNumber }) => removeEpicIssue(epicNumber, issueNumber, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.epicNumber] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
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
  return useMutation<BatchMembershipResponse, Error, { epicNumber: number; issueNumbers: number[] }>({
    mutationFn: ({ epicNumber, issueNumbers }) => batchAddEpicIssues(epicNumber, issueNumbers, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.epicNumber] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
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
  return useMutation<BatchMembershipResponse, Error, { epicNumber: number; issueNumbers: number[] }>({
    mutationFn: ({ epicNumber, issueNumbers }) => batchRemoveEpicIssues(epicNumber, issueNumbers, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.epicNumber] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      toast.success('Issues removed from Epic')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function startIssueMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (number: number) => startIssue(number, projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      toast.success('Issue started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useStartIssue() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(startIssueMutationOptions(projectId, queryClient))
}

export function useMarkEpicDone() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, number>({
    mutationFn: (number) => markEpicDone(number, projectId),
    onSuccess: (_data, number) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, number] })
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
  return useMutation<Epic, Error, number>({
    mutationFn: (number) => closeEpic(number, projectId),
    onSuccess: (_data, number) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, number] })
      toast.success('Epic closed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function pauseEpicMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ number, reason }: { number: number; reason?: string | null }) => pauseEpic(number, reason, projectId),
    onSuccess: (_data: Epic, variables: { number: number }) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.number] })
      toast.success('Epic paused')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function usePauseEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(pauseEpicMutationOptions(projectId, queryClient))
}

export function resumeEpicMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (number: number) => resumeEpic(number, projectId),
    onSuccess: (_data: Epic, number: number) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, number] })
      toast.success('Epic resumed')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useResumeEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(resumeEpicMutationOptions(projectId, queryClient))
}

export function startEpicMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (number: number) => startEpic(number, projectId),
    onSuccess: (_data: Epic, number: number) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, number] })
      toast.success('Epic started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useStartEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(startEpicMutationOptions(projectId, queryClient))
}

export function reopenEpicMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: (number: number) => reopenEpic(number, projectId),
    onSuccess: (_data: Epic, number: number) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, number] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      toast.success('Epic reopened')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useReopenEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(reopenEpicMutationOptions(projectId, queryClient))
}

export function useUpdateEpic() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<Epic, Error, { number: number; data: UpdateEpicInput }>({
    mutationFn: ({ number, data }) => updateEpic(number, data, projectId),
    onSuccess: (_data, variables) => {
      queryClient.invalidateQueries({ queryKey: ['epics'] })
      queryClient.invalidateQueries({ queryKey: ['epics', projectId, variables.number] })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      toast.success('Epic updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function epicEventsQueryOptions(
  number: number | null | undefined,
  projectId: string | null | undefined,
  enabled: boolean = true,
) {
  const safeNumber = typeof number === 'number' && number > 0 ? number : null
  return {
    queryKey: ['epics', projectId, safeNumber, 'events'],
    queryFn: () => getEpicEvents(safeNumber!, projectId),
    enabled: enabled && !!projectId && safeNumber !== null,
  }
}

export function useEpicEvents(number: number | null | undefined, enabled: boolean = true) {
  const { projectId } = useProject()
  return useQuery<StoredCloudEventDto[]>(epicEventsQueryOptions(number, projectId, enabled))
}
