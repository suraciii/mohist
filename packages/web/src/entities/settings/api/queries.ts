import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import type { QueryClient } from '@tanstack/react-query'
import { toast } from 'sonner'
import type { AgentRuntimeConfig, GeneralConfig, RuntimeConsistencyResponse, SystemInfo, SystemUpdateStartResponse, SystemUpdateStatusEnvelope, WorkflowProfileDetail, WorkflowProfileInfo } from '../model/types'
import { isActiveUpdateStatus, isSupersededStatus, isTerminalUpdateStatus } from '../model/updateOutcome'
import { includesWorkflowProfileId } from '../model/workflowProfileIds'
import { useProject } from '../../project/@x/project-context'
import type { OpencodeModelVariants, ProjectDefaultWorkflowProfile } from './client'
import { DEFAULT_AGENT_RUNTIME, isAgentRuntime, type AgentRuntime } from './client'
import { clearProjectDefaultWorkflowProfile, disableWorkflowProfile, enableWorkflowProfile, getAgentRuntime, getConfig, getLogLevel, getModel, getOpencodeModel, getOpencodeModelConfig, getOpencodeRuntime, getProjectDefaultWorkflowProfile, getRuntimeConsistency, getStageModels, getSystemInfo, getSystemUpdateStatus, getWorkflowProfile, getWorkflowProfiles, getModels, setLogLevel, setModel, setOpencodeModel, setProjectDefaultWorkflowProfile, setStageModel, startSystemUpdate, updateAgentRuntime, updateConfig, updateOpencodeModel } from './client'

type InvalidationClient = Pick<QueryClient, 'invalidateQueries'>

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

export function availableModelIdsQueryOptions(projectId: string | null | undefined, runtime: AgentRuntime | string = DEFAULT_AGENT_RUNTIME) {
  const normalized = isAgentRuntime(runtime) ? runtime : DEFAULT_AGENT_RUNTIME
  return {
    queryKey: ['opencode-model-ids', normalized, projectId],
    queryFn: async () => {
      const response = await getModels(projectId, normalized)
      return { models: response.models, modelVariants: response.modelVariants ?? {} }
    },
    enabled: !!projectId,
  }
}

export function useAvailableModelIds(runtime: AgentRuntime | string = DEFAULT_AGENT_RUNTIME) {
  const { projectId } = useProject()
  return useQuery<{ models: string[]; modelVariants: OpencodeModelVariants }>(availableModelIdsQueryOptions(projectId, runtime))
}

export function selectModelVariants(data: { models: string[]; modelVariants: OpencodeModelVariants } | undefined) {
  return data?.modelVariants ?? {}
}

export function useModelVariants(runtime: AgentRuntime | string = DEFAULT_AGENT_RUNTIME) {
  const { data } = useAvailableModelIds(runtime)
  return selectModelVariants(data)
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
  const { projectId } = useProject()
  return useQuery<WorkflowProfileInfo[]>({
    queryKey: ['workflow-templates', 'system', projectId],
    queryFn: () => getWorkflowProfiles(projectId),
    enabled: !!projectId,
  })
}

export function useAllWorkflowProfiles() {
  return useQuery<WorkflowProfileInfo[]>({
    queryKey: ['workflow-templates', 'system'],
    queryFn: () => getWorkflowProfiles(),
  })
}

export type WorkflowProfileFetcher = typeof getWorkflowProfile

export function useWorkflowProfile(
  id: string | null,
  fetcher: WorkflowProfileFetcher = getWorkflowProfile,
) {
  return useQuery<WorkflowProfileDetail>({
    queryKey: ['workflow-profile', id],
    queryFn: () => fetcher(id!),
    enabled: !!id,
  })
}

export function projectDefaultWorkflowProfileQueryOptions(projectId: string | null | undefined) {
  return {
    queryKey: ['project-workflow-profile', projectId],
    queryFn: () => getProjectDefaultWorkflowProfile(projectId),
    enabled: !!projectId,
  }
}

export function useProjectDefaultWorkflowProfile() {
  const { projectId } = useProject()
  return useQuery<ProjectDefaultWorkflowProfile>(projectDefaultWorkflowProfileQueryOptions(projectId))
}

export function setProjectDefaultWorkflowProfileMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: ({ templateId }: { templateId: string }) => setProjectDefaultWorkflowProfile(projectId, templateId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['project-workflow-profile', projectId] })
      toast.success('Project default workflow updated')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useSetProjectDefaultWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(setProjectDefaultWorkflowProfileMutationOptions(projectId, queryClient))
}

export function clearProjectDefaultWorkflowProfileMutationOptions(projectId: string | null | undefined, queryClient: InvalidationClient) {
  return {
    mutationFn: () => clearProjectDefaultWorkflowProfile(projectId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['project-workflow-profile', projectId] })
      toast.success('Project default workflow cleared')
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Request failed')
    },
  }
}

export function useClearProjectDefaultWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation(clearProjectDefaultWorkflowProfileMutationOptions(projectId, queryClient))
}

export function useDisableWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<void, Error, string>({
    mutationFn: (profileId) => disableWorkflowProfile(projectId, profileId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflow-templates', 'system', projectId] })
      queryClient.invalidateQueries({ queryKey: ['project-workflow-profile', projectId] })
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to disable workflow profile')
    },
  })
}

export function useEnableWorkflowProfile() {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  return useMutation<void, Error, string>({
    mutationFn: (profileId) => enableWorkflowProfile(projectId, profileId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['workflow-templates', 'system', projectId] })
      queryClient.invalidateQueries({ queryKey: ['project-workflow-profile', projectId] })
    },
    onError: (err: Error) => {
      toast.error(err.message || 'Failed to enable workflow profile')
    },
  })
}

export interface EffectiveDefaultWorkflowProfile {
  effectiveTemplateId: string
  source: 'project' | 'system' | 'none'
  configuredTemplateId: string | null
}

export function resolveEffectiveDefaultWorkflowProfile(
  projectProfile: ProjectDefaultWorkflowProfile | undefined,
  profiles: WorkflowProfileInfo[] | undefined,
): EffectiveDefaultWorkflowProfile {
  const configuredTemplateId = projectProfile?.defaultTemplateId ?? null
  const disabledIds = projectProfile?.disabledWorkflowProfileIds ?? []

  if (configuredTemplateId && !includesWorkflowProfileId(disabledIds, configuredTemplateId)) {
    return {
      effectiveTemplateId: configuredTemplateId,
      source: 'project',
      configuredTemplateId,
    }
  }

  const systemDefaultId = profiles?.find((p) => p.isDefault)?.id
  if (systemDefaultId) {
    return {
      effectiveTemplateId: systemDefaultId,
      source: 'system',
      configuredTemplateId,
    }
  }

  const firstEnabledId = profiles?.[0]?.id
  if (firstEnabledId) {
    return {
      effectiveTemplateId: firstEnabledId,
      source: 'system',
      configuredTemplateId,
    }
  }

  return {
    effectiveTemplateId: '',
    source: 'none',
    configuredTemplateId,
  }
}

export function useEffectiveDefaultWorkflowProfile(): EffectiveDefaultWorkflowProfile {
  const { data: projectProfile } = useProjectDefaultWorkflowProfile()
  const { data: profiles } = useWorkflowProfiles()
  return resolveEffectiveDefaultWorkflowProfile(projectProfile, profiles)
}
