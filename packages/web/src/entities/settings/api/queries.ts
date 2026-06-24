import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentRuntimeConfig, GeneralConfig, RuntimeConsistencyResponse, SystemInfo, SystemUpdateStartResponse, SystemUpdateStatusEnvelope, WorkflowProfileDetail, WorkflowProfileInfo } from '../model/types'
import { isActiveUpdateStatus, isSupersededStatus, isTerminalUpdateStatus } from '../model/updateOutcome'
import { useProject } from '../../project/@x/project-context'
import type { OpencodeModelVariants } from './client'
import { getAgentRuntime, getConfig, getLogLevel, getModel, getOpencodeModel, getOpencodeModelConfig, getOpencodeModels, getOpencodeRuntime, getRuntimeConsistency, getStageModels, getSystemInfo, getSystemUpdateStatus, getWorkflowProfile, getWorkflowProfiles, setLogLevel, setModel, setOpencodeModel, setStageModel, startSystemUpdate, updateAgentRuntime, updateConfig, updateOpencodeModel } from './client'

export function useConfig() {
  return useQuery<GeneralConfig, Error>({
    queryKey: ['config'],
    queryFn: () => getConfig(),
  })
}

interface UpdateConfigContext {
  previousConfig?: GeneralConfig
}

const CONFIG_KEY_TO_PROPERTY: Record<string, keyof GeneralConfig> = {
  'agent.timeout': 'agentTimeout',
  'agent.maxConcurrent': 'maxConcurrentAgents',
  'poll.interval': 'pollInterval',
}

export function useUpdateConfig() {
  const queryClient = useQueryClient()

  return useMutation<GeneralConfig, Error, { key: string; value: number }, UpdateConfigContext>({
    mutationFn: ({ key, value }) => updateConfig(key, value),
    onMutate: async ({ key, value }) => {
      await queryClient.cancelQueries({ queryKey: ['config'] })
      const previousConfig = queryClient.getQueryData<GeneralConfig>(['config'])

      const prop = CONFIG_KEY_TO_PROPERTY[key]
      if (prop) {
        queryClient.setQueryData<GeneralConfig>(['config'], (old) => {
          if (!old) return old
          return { ...old, [prop]: value }
        })
      }

      return { previousConfig }
    },
    onError: (_err, _variables, context) => {
      if (context?.previousConfig) {
        queryClient.setQueryData(['config'], context.previousConfig)
      }
      toast.error(_err.message || 'Request failed')
    },
    onSuccess: () => {
      toast.success('Setting updated')
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['config'] })
    },
  })
}

export function useOpencodeModel() {
  const { projectId } = useProject()
  return useQuery<{ model: string | null; variant: string | null }>({
    queryKey: ['opencode-model', projectId],
    queryFn: () => getOpencodeModel(projectId),
    enabled: !!projectId,
  })
}

export function useUpdateOpencodeModel() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<{ model: string | null; variant: string | null }, Error, { model: string | null; variant?: string | null }>({
    mutationFn: ({ model, variant }) => updateOpencodeModel(projectId, model, variant),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['opencode-model', projectId] })
      queryClient.invalidateQueries({ queryKey: ['stage-models', projectId] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAvailableModelIds() {
  const { projectId } = useProject()
  return useQuery<{ models: string[]; modelVariants: OpencodeModelVariants }>({
    queryKey: ['opencode-model-ids', projectId],
    queryFn: async () => {
      const response = await getOpencodeModels(projectId)
      return { models: response.models, modelVariants: response.modelVariants ?? {} }
    },
    enabled: !!projectId,
  })
}

export function useModelVariants() {
  const { data } = useAvailableModelIds()
  return data?.modelVariants ?? {}
}

export function useOpencodeRuntime() {
  return useQuery<{ mode: string; command: string; model: string | null; note: string }, Error>({
    queryKey: ['opencode-runtime'],
    queryFn: () => getOpencodeRuntime(),
  })
}

export function useSystemUpdate() {
  const queryClient = useQueryClient()
  return useMutation<SystemUpdateStartResponse, Error, void>({
    mutationFn: async () => startSystemUpdate(),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['system-update-status'] })
      toast.success('Update started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useSystemUpdateStatus(enabled = true) {
  return useQuery<SystemUpdateStatusEnvelope, Error>({
    queryKey: ['system-update-status'],
    queryFn: () => getSystemUpdateStatus(),
    enabled,
    retry: false,
    refetchInterval: (query) => {
      const status = query.state.data?.job?.status
      if (isActiveUpdateStatus(status)) {
        return 2000
      }
      if (isSupersededStatus(status) || isTerminalUpdateStatus(status)) {
        return false
      }
      return false
    },
  })
}

export function useModel() {
  return useQuery<{ model: string | null }>({
    queryKey: ['model'],
    queryFn: () => getModel(),
  })
}

export function useSetModel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => setModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['model'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useOpencodeModelConfig() {
  return useQuery<{ model: string | null }>({
    queryKey: ['opencode-model-config'],
    queryFn: () => getOpencodeModelConfig(),
  })
}

export function useSetOpencodeModelConfig() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => setOpencodeModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['opencode-model-config'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useLogLevel() {
  return useQuery<{ level: string }, Error>({
    queryKey: ['log-level'],
    queryFn: () => getLogLevel(),
  })
}

export function useSetLogLevel() {
  const queryClient = useQueryClient()
  return useMutation<{ level: string }, Error, string>({
    mutationFn: (level) => setLogLevel(level),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['log-level'] })
      queryClient.invalidateQueries({ queryKey: ['config'] })
      toast.success('Log level updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAgentRuntime() {
  return useQuery<AgentRuntimeConfig, Error>({
    queryKey: ['agent-runtime'],
    queryFn: () => getAgentRuntime(),
  })
}

export function useSetAgentRuntime() {
  const queryClient = useQueryClient()
  return useMutation<AgentRuntimeConfig, Error, Partial<AgentRuntimeConfig>>({
    mutationFn: (data) => updateAgentRuntime(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agent-runtime'] })
      queryClient.invalidateQueries({ queryKey: ['config'] })
      toast.success('Coder agent runtime updated')
    },
    onError: (err: Error) => {
      queryClient.invalidateQueries({ queryKey: ['agent-runtime'] })
      queryClient.invalidateQueries({ queryKey: ['config'] })
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useStageModels() {
  const { projectId } = useProject()
  return useQuery<{ stageModels: Record<string, string> | null; stageModelVariants: Record<string, string> | null }>({
    queryKey: ['stage-models', projectId],
    queryFn: () => getStageModels(projectId),
    enabled: !!projectId,
  })
}

export function useSetStageModels() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation({
    mutationFn: ({ stage, model, variant }: { stage: string; model: string | null; variant?: string | null }) => setStageModel(projectId, stage, model, variant),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['stage-models', projectId] })
      toast.success('Stage models updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useSystemInfo() {
  return useQuery<SystemInfo>({
    queryKey: ['system-info'],
    queryFn: () => getSystemInfo(),
  })
}

export function useRuntimeConsistency(enabled = true) {
  return useQuery<RuntimeConsistencyResponse, Error>({
    queryKey: ['system-consistency'],
    queryFn: () => getRuntimeConsistency(),
    enabled,
    retry: false,
  })
}

export function useWorkflowProfiles() {
  return useQuery<WorkflowProfileInfo[]>({
    queryKey: ['workflow-templates', 'system'],
    queryFn: () => getWorkflowProfiles(),
  })
}

export function useWorkflowProfile(id: string | null) {
  return useQuery<WorkflowProfileDetail>({
    queryKey: ['workflow-profile', id],
    queryFn: () => getWorkflowProfile(id!),
    enabled: !!id,
  })
}
