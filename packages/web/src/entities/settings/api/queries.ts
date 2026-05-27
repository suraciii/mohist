import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import { api } from '../../../shared/api/client'
import type { AgentRuntimeConfig, GeneralConfig, SystemInfo } from '../../../shared/api/types'

export function useConfig() {
  return useQuery<GeneralConfig, Error>({
    queryKey: ['config'],
    queryFn: () => api.getConfig(),
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
    mutationFn: ({ key, value }) => api.updateConfig(key, value),
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
  return useQuery<{ model: string | null }>({
    queryKey: ['opencode-model'],
    queryFn: () => api.getOpencodeModel(),
  })
}

export function useUpdateOpencodeModel() {
  const queryClient = useQueryClient()
  return useMutation<{ model: string | null }, Error, string | null>({
    mutationFn: (model) => api.updateOpencodeModel(model),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['opencode-model'] })
      toast.success('Model updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAvailableModelIds() {
  return useQuery<string[]>({
    queryKey: ['opencode-model-ids'],
    queryFn: async () => {
      const response = await api.getOpencodeModels()
      return response.models
    },
  })
}

export function useOpencodeRuntime() {
  return useQuery<{ mode: string; command: string; model: string | null; note: string }, Error>({
    queryKey: ['opencode-runtime'],
    queryFn: () => api.getOpencodeRuntime(),
  })
}

export function useRebuildSystem() {
  const queryClient = useQueryClient()
  return useMutation<{ success: boolean }, Error, void>({
    mutationFn: async () => {
      throw new Error('Server rebuild is not managed by Mohist Web')
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['status'] })
      toast.success('Rebuild started')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useModel() {
  return useQuery<{ model: string | null }>({
    queryKey: ['model'],
    queryFn: () => api.getModel(),
  })
}

export function useSetModel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => api.setModel(model),
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
    queryFn: () => api.getOpencodeModelConfig(),
  })
}

export function useSetOpencodeModelConfig() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (model: string | null) => api.setOpencodeModel(model),
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
  return useQuery<{ level: string }>({
    queryKey: ['log-level'],
    queryFn: () => api.getLogLevel(),
  })
}

export function useSetLogLevel() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (level: string) => api.setLogLevel(level),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['log-level'] })
      toast.success('Log level updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useAgentRuntime() {
  return useQuery<AgentRuntimeConfig>({
    queryKey: ['agent-runtime'],
    queryFn: () => api.getAgentRuntime(),
  })
}

export function useSetAgentRuntime() {
  const queryClient = useQueryClient()
  return useMutation<AgentRuntimeConfig, Error, Partial<AgentRuntimeConfig>>({
    mutationFn: (data) => api.updateAgentRuntime(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['agent-runtime'] })
      toast.success('Coder agent runtime updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  })
}

export function useStageModels() {
  return useQuery<{ stageModels: Record<string, string> | null }>({
    queryKey: ['stage-models'],
    queryFn: () => api.getStageModels(),
  })
}

export function useSetStageModels() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (stageModels: Record<string, string> | null) => api.setStageModels(stageModels),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['stage-models'] })
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
    queryFn: () => api.getSystemInfo(),
  })
}
