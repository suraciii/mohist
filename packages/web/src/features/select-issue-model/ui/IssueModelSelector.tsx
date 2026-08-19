import React, { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import fuzzysort from 'fuzzysort'
import { Command as CommandRoot } from 'cmdk'
import {
  getWorkflowProfileAgentRuntime,
  isAgentRuntime,
  useAvailableModelIds,
  useEffectiveDefaultWorkflowProfile,
  useModelVariants,
  useOpencodeModel,
  useWorkflowProfiles,
} from '../../../entities/settings'
import type { AgentRuntime } from '../../../entities/settings'
import {
  getIssueWorkflowVariables,
  isTerminalWorkflowRunStatus,
  issueDetailKeys,
  issueListKeys,
  patchIssueWorkflowDefinitionVar,
  patchIssueWorkflowStageDefinitionVar,
  useWorkflowRunDetail,
} from '../../../entities/issue'
import { useQueryClient } from '@tanstack/react-query'
import { ModelSelect, ModelVariantChips, describeModel } from '../../../shared/ui/ModelSelect'
import { variantListFor } from '../../../shared/ui/model-variants'
import { useProject } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { cn } from '@/shared/lib/utils'
import { CommandGroup } from '@/shared/ui/components/command'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'

const RECENT_KEY = 'mohist:recent-issue-models'
const MAX_RECENT = 5
const ISSUE_STAGES = ['plan', 'build', 'check', 'integrate'] as const

interface Props {
  issueNumber: number
  currentModel?: string | null
  currentStageModels?: Record<string, string> | null
  workflowRunId?: string | null
  workflowProfileId?: string | null
  dependencies?: IssueModelSelectorDependencies
}

export interface IssueModelSelectorDependencies {
  useAvailableModelIds: typeof useAvailableModelIds
  useModelVariants: typeof useModelVariants
  useOpencodeModel: typeof useOpencodeModel
  useWorkflowProfiles: typeof useWorkflowProfiles
  useEffectiveDefaultWorkflowProfile: typeof useEffectiveDefaultWorkflowProfile
  useWorkflowRunDetail: typeof useWorkflowRunDetail
  getIssueWorkflowVariables: typeof getIssueWorkflowVariables
  patchIssueWorkflowDefinitionVar: typeof patchIssueWorkflowDefinitionVar
  patchIssueWorkflowStageDefinitionVar: typeof patchIssueWorkflowStageDefinitionVar
}

const defaultDependencies: IssueModelSelectorDependencies = {
  useAvailableModelIds,
  useModelVariants,
  useOpencodeModel,
  useWorkflowProfiles,
  useEffectiveDefaultWorkflowProfile,
  useWorkflowRunDetail,
  getIssueWorkflowVariables,
  patchIssueWorkflowDefinitionVar,
  patchIssueWorkflowStageDefinitionVar,
}

function agentModel(vars?: Record<string, unknown> | null): string | null {
  const agent = vars?.agent
  if (!agent || typeof agent !== 'object' || Array.isArray(agent)) return null
  const model = (agent as Record<string, unknown>).model
  return typeof model === 'string' && model.length > 0 ? model : null
}

function agentVariant(vars?: Record<string, unknown> | null): string | null {
  const agent = vars?.agent
  if (!agent || typeof agent !== 'object' || Array.isArray(agent)) return null
  const variant = (agent as Record<string, unknown>).variant
  return typeof variant === 'string' && variant.length > 0 ? variant : null
}

function agentReasoningEffort(vars?: Record<string, unknown> | null): string | null {
  const agent = vars?.agent
  if (!agent || typeof agent !== 'object' || Array.isArray(agent)) return null
  const effort = (agent as Record<string, unknown>).reasoningEffort
  return typeof effort === 'string' && effort.length > 0 ? effort : null
}

function stageModelMap(
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null,
): Record<string, string> {
  const result: Record<string, string> = {}
  if (!stages) return result
  for (const [stage, stageVars] of Object.entries(stages)) {
    const model = agentModel(stageVars?.vars)
    if (model) result[stage] = model
  }
  return result
}

function stageVariantMap(
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null,
): Record<string, string> {
  const result: Record<string, string> = {}
  if (!stages) return result
  for (const [stage, stageVars] of Object.entries(stages)) {
    const variant = agentVariant(stageVars?.vars)
    if (variant) result[stage] = variant
  }
  return result
}

function stageReasoningEffortMap(
  stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null,
): Record<string, string> {
  const result: Record<string, string> = {}
  if (!stages) return result
  for (const [stage, stageVars] of Object.entries(stages)) {
    const effort = agentReasoningEffort(stageVars?.vars)
    if (effort) result[stage] = effort
  }
  return result
}

function getRecent(): string[] {
  try {
    return JSON.parse(localStorage.getItem(RECENT_KEY) || '[]')
  } catch {
    return []
  }
}

function addRecent(modelId: string) {
  const recent = getRecent().filter((id) => id !== modelId)
  recent.unshift(modelId)
  localStorage.setItem(RECENT_KEY, JSON.stringify(recent.slice(0, MAX_RECENT)))
}

function SearchIcon() {
  return (
    <svg className="h-4 w-4 text-muted-foreground/70" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M9 3.5a5.5 5.5 0 100 11 5.5 5.5 0 000-11zM2 9a7 7 0 1112.452 4.391l3.328 3.329a.75.75 0 11-1.06 1.06l-3.329-3.328A7 7 0 012 9z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function ChevronDownIcon() {
  return (
    <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function ChevronRightIcon({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 20 20" fill="currentColor">
      <path
        fillRule="evenodd"
        d="M7.21 8.145a.75.75 0 011.06-.02L10 9.835l1.73-1.71a.75.75 0 011.04 1.08l-2.25 2.22a.75.75 0 01-1.04 0l-2.25-2.22a.75.75 0 01-.02-1.06z"
        clipRule="evenodd"
      />
    </svg>
  )
}

function modelDisplayName(modelId: string): string {
  return describeModel(modelId).name
}

export function IssueModelSelector({
  issueNumber,
  currentModel,
  currentStageModels,
  workflowRunId,
  workflowProfileId,
  dependencies = defaultDependencies,
}: Props) {
  const {
    useAvailableModelIds,
    useOpencodeModel,
    useWorkflowProfiles,
    useEffectiveDefaultWorkflowProfile,
    useWorkflowRunDetail,
    getIssueWorkflowVariables,
    patchIssueWorkflowDefinitionVar,
    patchIssueWorkflowStageDefinitionVar,
  } = dependencies
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { data: workflowProfiles } = useWorkflowProfiles()
  const { effectiveTemplateId } = useEffectiveDefaultWorkflowProfile()
  const selectedProfileId = workflowProfileId ?? effectiveTemplateId
  const profileRuntime = getWorkflowProfileAgentRuntime(workflowProfiles, selectedProfileId)
  const {
    data: workflowRun,
    isLoading: isWorkflowRunLoading,
    error: workflowRunError,
  } = useWorkflowRunDetail(workflowRunId)
  const hasActiveRun = !!workflowRun && !isTerminalWorkflowRunStatus(workflowRun.status.status)
  const runProfileRuntime = getWorkflowProfileAgentRuntime(workflowProfiles, workflowRun?.workflowProfileId)
  const selectedRuntime: AgentRuntime | null = !workflowRunId
    ? profileRuntime
    : isWorkflowRunLoading || workflowRunError || !workflowRun
      ? null
      : !hasActiveRun
        ? profileRuntime
        : workflowRun.agentAction != null
          ? isAgentRuntime(workflowRun.agentRuntime)
            ? workflowRun.agentRuntime
            : null
          : runProfileRuntime
  const catalog = useAvailableModelIds(selectedRuntime)
  const { data: availableModels, isLoading, error } = catalog
  const { data: opencodeModelData } = useOpencodeModel()
  const modelVariantsMap = availableModels?.modelVariants ?? {}
  const reasoningEffortsMap = availableModels?.reasoningEfforts ?? {}
  const levelMap = selectedRuntime === 'pi' ? reasoningEffortsMap : modelVariantsMap
  const [searchQuery, setSearchQuery] = useState('')
  const chipRefs = useRef<Record<string, Array<HTMLButtonElement | null>>>({})
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})
  const [localStageVariants, setLocalStageVariants] = useState<Record<string, string>>({})
  const [localStageReasoningEfforts, setLocalStageReasoningEfforts] = useState<Record<string, string>>({})
  const [localWorkflowModel, setLocalWorkflowModel] = useState<string | null>(null)
  const [localWorkflowVariant, setLocalWorkflowVariant] = useState<string | null>(null)
  const [localWorkflowReasoningEffort, setLocalWorkflowReasoningEffort] = useState<string | null>(null)
  const [popoverOpen, setPopoverOpen] = useState(false)

  useEffect(() => {
    let cancelled = false
    if (!projectId) {
      setLocalWorkflowModel(null)
      setLocalWorkflowVariant(null)
      setLocalWorkflowReasoningEffort(null)
      setLocalStageModels(currentStageModels ?? {})
      setLocalStageVariants({})
      setLocalStageReasoningEfforts({})
      return
    }

    getIssueWorkflowVariables(issueNumber, projectId)
      .then((variables) => {
        if (cancelled) return
        setLocalWorkflowModel(agentModel(variables.vars))
        setLocalWorkflowVariant(agentVariant(variables.vars))
        setLocalWorkflowReasoningEffort(agentReasoningEffort(variables.vars))
        setLocalStageModels(stageModelMap(variables.stages))
        setLocalStageVariants(stageVariantMap(variables.stages))
        setLocalStageReasoningEfforts(stageReasoningEffortMap(variables.stages))
      })
      .catch((err) => {
        if (cancelled) return
        console.error('Failed to load issue workflow variables:', err)
        setLocalStageModels(currentStageModels ?? {})
        setLocalStageVariants({})
        setLocalStageReasoningEfforts({})
      })

    return () => {
      cancelled = true
    }
  }, [issueNumber, projectId, currentStageModels])

  const stageCatalogs = {
    plan: catalog,
    build: catalog,
    check: catalog,
    integrate: catalog,
  }

  const allModels: string[] = availableModels?.models ?? []
  const recentModelIds = getRecent()
  const recentModels = recentModelIds.filter((id) => allModels.includes(id))

  const searchableModels = allModels.map((id) => ({ id, display: modelDisplayName(id) }))
  const filteredResults = searchQuery.trim()
    ? fuzzysort.go(searchQuery, searchableModels, { keys: ['display', 'id'] }).map((r) => r.obj.id)
    : []

  const displayedModels = searchQuery.trim() ? filteredResults : allModels

  const handleSelect = useCallback(
    async (modelId: string) => {
      try {
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowDefinitionVar(
          issueNumber,
          'agent',
          { model: modelId, variant: null, ...(localWorkflowReasoningEffort ? { reasoningEffort: null } : {}) },
          projectId,
        )
        setLocalWorkflowModel(modelId)
        setLocalWorkflowVariant(null)
        setLocalWorkflowReasoningEffort(null)
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
        setPopoverOpen(false)
      } catch (err) {
        console.error('Failed to update issue model:', err)
      }
    },
    [issueNumber, projectId, queryClient],
  )

  const handleSelectWithVariant = useCallback(
    async (modelId: string, variant: string) => {
      try {
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowDefinitionVar(
          issueNumber,
          'agent',
          { model: modelId, variant, ...(localWorkflowReasoningEffort ? { reasoningEffort: null } : {}) },
          projectId,
        )
        setLocalWorkflowModel(modelId)
        setLocalWorkflowVariant(variant)
        setLocalWorkflowReasoningEffort(null)
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
        setPopoverOpen(false)
      } catch (err) {
        console.error('Failed to update issue model with variant:', err)
      }
    },
    [issueNumber, projectId, queryClient],
  )

  const handleSelectWithReasoningEffort = useCallback(
    async (modelId: string, effort: string) => {
      try {
        if (!projectId) throw new Error('Project is required')
        const preservedVariant = modelId === localWorkflowModel ? localWorkflowVariant : null
        await patchIssueWorkflowDefinitionVar(
          issueNumber,
          'agent',
          { model: modelId, variant: preservedVariant, reasoningEffort: effort },
          projectId,
        )
        setLocalWorkflowModel(modelId)
        setLocalWorkflowVariant(preservedVariant)
        setLocalWorkflowReasoningEffort(effort)
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
        setPopoverOpen(false)
      } catch (err) {
        console.error('Failed to update issue model reasoning effort:', err)
      }
    },
    [issueNumber, localWorkflowModel, localWorkflowVariant, projectId, queryClient],
  )

  const handleSelectLevel = useCallback(
    (modelId: string, level: string | null) => {
      if (selectedRuntime === 'pi') {
        void handleSelectWithReasoningEffort(modelId, level!)
      } else {
        void handleSelectWithVariant(modelId, level ?? '')
      }
    },
    [handleSelectWithReasoningEffort, handleSelectWithVariant, selectedRuntime],
  )

  const handleClear = useCallback(async () => {
    try {
      if (!projectId) throw new Error('Project is required')
      await patchIssueWorkflowDefinitionVar(
        issueNumber,
        'agent',
        { model: null, variant: null, ...(localWorkflowReasoningEffort ? { reasoningEffort: null } : {}) },
        projectId,
      )
      setLocalWorkflowModel(null)
      setLocalWorkflowVariant(null)
      setLocalWorkflowReasoningEffort(null)
      queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
      queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      setPopoverOpen(false)
    } catch (err) {
      console.error('Failed to clear issue model:', err)
    }
  }, [issueNumber, projectId, queryClient])

  const handleSetStageModel = useCallback(
    async (stage: string, modelId: string) => {
      try {
        const updated = { ...localStageModels, [stage]: modelId }
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowStageDefinitionVar(
          issueNumber,
          stage,
          'agent',
          { model: modelId, variant: null, ...(localStageReasoningEfforts[stage] ? { reasoningEffort: null } : {}) },
          projectId,
        )
        setLocalStageModels(updated)
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        setLocalStageReasoningEfforts((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      } catch (err) {
        console.error('Failed to update stage model:', err)
      }
    },
    [issueNumber, localStageModels, projectId, queryClient],
  )

  const handleClearStageModel = useCallback(
    async (stage: string) => {
      try {
        const updated = { ...localStageModels }
        delete updated[stage]
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowStageDefinitionVar(
          issueNumber,
          stage,
          'agent',
          { model: null, variant: null, ...(localStageReasoningEfforts[stage] ? { reasoningEffort: null } : {}) },
          projectId,
        )
        setLocalStageModels(updated)
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        setLocalStageReasoningEfforts((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      } catch (err) {
        console.error('Failed to clear stage model:', err)
      }
    },
    [issueNumber, localStageModels, projectId, queryClient],
  )

  const handleSetStageVariant = useCallback(
    async (stage: string, modelId: string, variant: string | null) => {
      try {
        if (!projectId) throw new Error('Project is required')
        if (variant) {
          await patchIssueWorkflowStageDefinitionVar(
            issueNumber,
            stage,
            'agent',
            { model: modelId, variant, ...(localStageReasoningEfforts[stage] ? { reasoningEffort: null } : {}) },
            projectId,
          )
        } else {
          await patchIssueWorkflowStageDefinitionVar(
            issueNumber,
            stage,
            'agent',
            { model: modelId, variant: null, ...(localStageReasoningEfforts[stage] ? { reasoningEffort: null } : {}) },
            projectId,
          )
        }
        setLocalStageModels((prev) => ({ ...prev, [stage]: modelId }))
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          if (variant) next[stage] = variant
          else delete next[stage]
          return next
        })
        setLocalStageReasoningEfforts((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      } catch (err) {
        console.error('Failed to update stage model variant:', err)
      }
    },
    [issueNumber, localStageReasoningEfforts, projectId, queryClient],
  )

  const handleSetStageReasoningEffort = useCallback(
    async (stage: string, modelId: string, effort: string | null) => {
      try {
        if (!projectId) throw new Error('Project is required')
        const preservedVariant = modelId === localStageModels[stage] ? (localStageVariants[stage] ?? null) : null
        await patchIssueWorkflowStageDefinitionVar(
          issueNumber,
          stage,
          'agent',
          { model: modelId, variant: preservedVariant, reasoningEffort: effort },
          projectId,
        )
        setLocalStageModels((prev) => ({ ...prev, [stage]: modelId }))
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          if (preservedVariant) next[stage] = preservedVariant
          else delete next[stage]
          return next
        })
        setLocalStageReasoningEfforts((prev) => {
          const next = { ...prev }
          if (effort) next[stage] = effort
          else delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: issueDetailKeys.detail(projectId, issueNumber), exact: true })
        queryClient.invalidateQueries({ queryKey: issueListKeys.project(projectId) })
      } catch (err) {
        console.error('Failed to update stage model reasoning effort:', err)
      }
    },
    [issueNumber, localStageModels, localStageVariants, projectId, queryClient],
  )

  const handleChipKeyDown = useCallback(
    (event: React.KeyboardEvent, modelId: string, chipIndex: number) => {
      const variants = variantListFor(modelId, levelMap)
      if (variants.length === 0) return

      if (event.key === 'ArrowLeft') {
        event.preventDefault()
        if (chipIndex > 0) {
          chipRefs.current[modelId]?.[chipIndex - 1]?.focus()
        } else {
          const input = document.querySelector<HTMLInputElement>('[cmdk-input]')
          input?.focus()
        }
      } else if (event.key === 'ArrowRight') {
        if (chipIndex < variants.length - 1) {
          event.preventDefault()
          chipRefs.current[modelId]?.[chipIndex + 1]?.focus()
        }
      } else if (event.key === 'Enter') {
        event.preventDefault()
        handleSelectLevel(modelId, variants[chipIndex])
      }
    },
    [levelMap, handleSelectLevel],
  )

  const handleCommandKeyDown = useCallback(
    (event: React.KeyboardEvent) => {
      const isRightOrTab = event.key === 'ArrowRight' || (event.key === 'Tab' && !event.shiftKey)
      if (!isRightOrTab) return

      const target = event.target as HTMLElement | null
      if (target?.closest('[data-variant-chip]')) return

      const activeItem = (event.currentTarget as HTMLElement).querySelector(
        '[data-selected="true"][data-model-id]',
      ) as HTMLElement | null
      if (!activeItem) return
      const activeModelId = activeItem.getAttribute('data-model-id')
      if (!activeModelId) return

      const variants = variantListFor(activeModelId, levelMap)
      if (variants.length === 0) return

      event.preventDefault()
      chipRefs.current[activeModelId]?.[0]?.focus()
    },
    [levelMap],
  )

  const groupedModels = useMemo(() => {
    const map = new Map<string, string[]>()
    for (const id of displayedModels) {
      const provider = id.split('/')[0] || 'other'
      const list = map.get(provider)
      if (list) list.push(id)
      else map.set(provider, [id])
    }
    return map
  }, [displayedModels])

  const defaultModelId = opencodeModelData?.model ?? null

  const configuredModel = localWorkflowModel ?? currentModel

  const resolvedModelId = configuredModel ?? defaultModelId
  const resolvedVariant = localWorkflowVariant
  const currentModelDisplay = resolvedModelId
    ? describeModel(resolvedModelId).name + (resolvedVariant ? ` · ${resolvedVariant}` : '')
    : 'Use default'
  const currentModelFullId = resolvedModelId
    ? describeModel(resolvedModelId).fullId + (resolvedVariant ? `:${resolvedVariant}` : '')
    : null

  if (selectedRuntime === null) {
    const configuredStageEntries = ISSUE_STAGES.filter((stage) => !!localStageModels[stage])
    if (!resolvedModelId && configuredStageEntries.length === 0) return null

    return (
      <div className="space-y-1" data-testid="issue-model-read-only">
        {resolvedModelId && (
          <div>
            <label className="block text-sm text-muted-foreground">Coder Model</label>
            <ModelSelect
              id="issue-coder-model-read-only"
              value={resolvedModelId}
              placeholder="Use default"
              models={[]}
              onChange={() => undefined}
              modelVariants={resolvedVariant ? { [resolvedModelId]: [resolvedVariant] } : {}}
              modelReasoningEfforts={
                localWorkflowReasoningEffort ? { [resolvedModelId]: [localWorkflowReasoningEffort] } : {}
              }
              valueVariant={resolvedVariant}
              valueReasoningEffort={localWorkflowReasoningEffort}
              disabled
            />
          </div>
        )}
        {configuredStageEntries.length > 0 && (
          <div className="pt-2 border-t">
            <span className="text-xs text-muted-foreground">Per-stage overrides</span>
            <div className="mt-3 space-y-2 pl-5">
              {configuredStageEntries.map((stage) => {
                const stageModel = localStageModels[stage]
                const stageVariant = localStageVariants[stage] ?? null
                return (
                  <div key={stage} className="flex items-center gap-2">
                    <span className="text-xs font-medium text-muted-foreground w-16 capitalize shrink-0">{stage}</span>
                    <div className="flex-1">
                      <ModelSelect
                        id={`issue-stage-model-${stage}`}
                        value={stageModel}
                        placeholder="Default"
                        models={[]}
                        onChange={() => undefined}
                        size="compact"
                        modelVariants={stageVariant ? { [stageModel]: [stageVariant] } : {}}
                        modelReasoningEfforts={
                          localStageReasoningEfforts[stage] ? { [stageModel]: [localStageReasoningEfforts[stage]] } : {}
                        }
                        valueVariant={stageVariant}
                        valueReasoningEffort={localStageReasoningEfforts[stage] ?? null}
                        disabled
                      />
                    </div>
                  </div>
                )
              })}
            </div>
          </div>
        )}
      </div>
    )
  }

  return (
    <div className="space-y-1">
      <label className="block text-sm text-muted-foreground">Coder Model</label>
      <Popover open={popoverOpen} onOpenChange={setPopoverOpen}>
        <PopoverTrigger
          render={
            <Button
              variant="outline"
              aria-label="Coder Model"
              data-testid="issue-coder-model-trigger"
              className={`w-full justify-between gap-1.5 min-h-[44px] md:min-h-0 ${
                popoverOpen
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                  : configuredModel
                    ? 'border-blue-200 bg-blue-50 text-blue-700'
                    : 'border-gray-300 bg-background text-foreground hover:bg-muted'
              }`}
            />
          }
        >
          {currentModelFullId ? (
            <div className="flex min-w-0 flex-1 flex-col items-start gap-0 text-left leading-tight">
              <span className="w-full truncate font-medium">{currentModelDisplay}</span>
              <span className="w-full truncate text-xs font-normal opacity-80" title={currentModelFullId}>
                {currentModelFullId}
              </span>
            </div>
          ) : (
            <span className="truncate">{currentModelDisplay}</span>
          )}
          <ChevronDownIcon />
        </PopoverTrigger>
        <PopoverContent className="w-80 p-0" align="end">
          <CommandRoot filter={() => 1} value={configuredModel ?? undefined} onKeyDown={handleCommandKeyDown}>
            <div className="p-2">
              <div className="relative">
                <div className="absolute left-3 top-1/2 -translate-y-1/2">
                  <SearchIcon />
                </div>
                <CommandRoot.Input
                  value={searchQuery}
                  onValueChange={setSearchQuery}
                  placeholder="Search models..."
                  className="flex h-9 w-full rounded-md border border-input bg-transparent pl-9 py-1 pr-3 text-base shadow-sm transition-colors outline-none placeholder:text-muted-foreground md:text-sm"
                />
              </div>
            </div>

            <CommandRoot.List className="max-h-80 scroll-py-1 overflow-x-hidden overflow-y-auto overscroll-y-contain outline-none border-t">
              {isLoading && (
                <div className="px-3 py-6 text-center text-sm text-muted-foreground/70">Loading models...</div>
              )}

              {error && !isLoading && (
                <div className="px-3 py-6 text-center text-sm text-red-500">
                  Failed to load models: {(error as Error).message}
                </div>
              )}

              {!isLoading && !error && configuredModel && !searchQuery.trim() && (
                <CommandGroup
                  heading="Override"
                  value="__override__"
                  className="**:[[cmdk-group-heading]]:sticky **:[[cmdk-group-heading]]:top-0 **:[[cmdk-group-heading]]:z-10 **:[[cmdk-group-heading]]:bg-muted"
                >
                  <CommandRoot.Item
                    value="__clear__"
                    onSelect={handleClear}
                    className="px-3 py-2 text-sm text-amber-700 cursor-pointer hover:bg-amber-50 data-selected:bg-amber-50 data-selected:text-amber-700"
                  >
                    <span className="font-medium">
                      Use default{defaultModelId ? ` (${describeModel(defaultModelId).name})` : ''}
                    </span>
                  </CommandRoot.Item>
                </CommandGroup>
              )}

              {!isLoading && !error && recentModels.length > 0 && !searchQuery.trim() && (
                <CommandGroup
                  heading="Recent"
                  value="__recent__"
                  className="**:[[cmdk-group-heading]]:sticky **:[[cmdk-group-heading]]:top-0 **:[[cmdk-group-heading]]:z-10 **:[[cmdk-group-heading]]:bg-muted"
                >
                  {recentModels.map((modelId) => {
                    if (!chipRefs.current[modelId]) chipRefs.current[modelId] = []
                    const variants = variantListFor(modelId, levelMap)
                    const isSelected = modelId === configuredModel
                    return (
                      <CommandRoot.Item
                        key={modelId}
                        value={modelId}
                        data-model-id={modelId}
                        onSelect={() => handleSelect(modelId)}
                        className={cn(
                          'flex w-full items-center justify-between gap-2 rounded-none cursor-pointer px-3 py-1.5',
                          isSelected && 'bg-accent text-accent-foreground',
                        )}
                      >
                        <div className="flex min-w-0 flex-col items-start">
                          <span className="w-full truncate font-medium text-sm">{modelDisplayName(modelId)}</span>
                          <span className="w-full truncate text-muted-foreground text-xs">{modelId}</span>
                        </div>
                        {variants.length > 0 && (
                          <ModelVariantChips
                            modelId={modelId}
                            modelVariants={levelMap}
                            activeVariant={isSelected ? (localWorkflowReasoningEffort ?? localWorkflowVariant) : null}
                            baseTestId={`issue-coder-model-variant-${modelId}`}
                            chipRefs={chipRefs.current[modelId]}
                            onChipKeyDown={(e, idx) => handleChipKeyDown(e, modelId, idx)}
                            onSelect={handleSelectLevel}
                          />
                        )}
                      </CommandRoot.Item>
                    )
                  })}
                </CommandGroup>
              )}

              {!isLoading && !error && displayedModels.length === 0 && (
                <div className="px-3 py-6 text-center text-sm text-muted-foreground/70">No models found</div>
              )}

              {!isLoading &&
                !error &&
                Array.from(groupedModels.entries()).map(([provider, models]) => (
                  <CommandGroup
                    key={provider}
                    heading={provider}
                    value={provider}
                    className="**:[[cmdk-group-heading]]:sticky **:[[cmdk-group-heading]]:top-0 **:[[cmdk-group-heading]]:z-10 **:[[cmdk-group-heading]]:bg-muted"
                  >
                    {models.map((modelId) => {
                      if (!chipRefs.current[modelId]) chipRefs.current[modelId] = []
                      const variants = variantListFor(modelId, levelMap)
                      const isSelected = modelId === configuredModel
                      return (
                        <CommandRoot.Item
                          key={modelId}
                          value={modelId}
                          data-model-id={modelId}
                          onSelect={() => handleSelect(modelId)}
                          className={cn(
                            'flex w-full items-center justify-between gap-2 rounded-none cursor-pointer px-3 py-1.5',
                            isSelected && 'bg-accent text-accent-foreground',
                          )}
                        >
                          <div className="flex min-w-0 flex-col items-start">
                            <span className="w-full truncate font-medium text-sm">{modelDisplayName(modelId)}</span>
                            <span className="w-full truncate text-muted-foreground text-xs">{modelId}</span>
                          </div>
                          {variants.length > 0 && (
                            <ModelVariantChips
                              modelId={modelId}
                              modelVariants={levelMap}
                              activeVariant={isSelected ? (localWorkflowReasoningEffort ?? localWorkflowVariant) : null}
                              baseTestId={`issue-coder-model-variant-${modelId}`}
                              chipRefs={chipRefs.current[modelId]}
                              onChipKeyDown={(e, idx) => handleChipKeyDown(e, modelId, idx)}
                              onSelect={handleSelectLevel}
                            />
                          )}
                        </CommandRoot.Item>
                      )
                    })}
                  </CommandGroup>
                ))}
            </CommandRoot.List>

            <div className="border-t p-2 text-xs text-muted-foreground/70 text-center">
              Use ↑↓ to navigate, Enter to select, Esc to close
            </div>
          </CommandRoot>
        </PopoverContent>
      </Popover>
      {configuredModel && (
        <p className="text-xs text-muted-foreground/70">Override active. Falls back to default when cleared.</p>
      )}

      <div className="pt-2 border-t">
        <Button
          variant="ghost"
          onClick={() => setAdvancedOpen(!advancedOpen)}
          className="flex items-center gap-1.5 w-full justify-start h-auto px-0 py-0 font-normal hover:bg-transparent"
        >
          <ChevronRightIcon
            className={`h-3.5 w-3.5 text-muted-foreground/70 transition-transform ${advancedOpen ? 'rotate-90' : ''}`}
          />
          <span className="text-xs text-muted-foreground">Per-stage overrides</span>
          {Object.keys(localStageModels).length > 0 && (
            <span className="text-xs text-blue-500">({Object.keys(localStageModels).length})</span>
          )}
        </Button>

        {advancedOpen && (
          <div className="mt-3 space-y-2 pl-5">
            {ISSUE_STAGES.map((stage) => {
              const stageModel = localStageModels[stage] ?? null
              const stageVariant = localStageVariants[stage] ?? null
              const stageCatalog = stageCatalogs[stage]
              const stageModels = stageCatalog.data?.models ?? []
              const stageVariants = stageCatalog.data?.modelVariants ?? {}
              return (
                <div key={stage} className="flex items-center gap-2">
                  <span className="text-xs font-medium text-muted-foreground w-16 capitalize shrink-0">{stage}</span>
                  <div className="flex-1">
                    <ModelSelect
                      id={`issue-stage-model-${stage}`}
                      value={stageModel}
                      placeholder="Default"
                      models={stageModels}
                      onChange={(modelId) => handleSetStageModel(stage, modelId)}
                      onClear={() => handleClearStageModel(stage)}
                      allowClear={!!stageModel}
                      size="compact"
                      modelVariants={stageVariants}
                      modelReasoningEfforts={selectedRuntime === 'pi' ? reasoningEffortsMap : undefined}
                      valueVariant={stageVariant}
                      valueReasoningEffort={localStageReasoningEfforts[stage] ?? null}
                      onChangeModelVariant={(modelId, variant) => handleSetStageVariant(stage, modelId, variant)}
                      onChangeModelReasoningEffort={(modelId, effort) =>
                        handleSetStageReasoningEffort(stage, modelId, effort)
                      }
                    />
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>
    </div>
  )
}
