import React, { useState, useEffect, useCallback, useRef } from 'react'
import fuzzysort from 'fuzzysort'
import { useAvailableModelIds, useModelVariants, useOpencodeModel } from '../../../entities/settings'
import { getIssueWorkflowVariables, patchIssueWorkflowDefinitionVar, patchIssueWorkflowStageDefinitionVar } from '../../../entities/issue'
import { useQueryClient } from '@tanstack/react-query'
import { ModelSelect, ModelVariantChips, describeModel } from '../../../shared/ui/ModelSelect'
import { useProject } from '../../../entities/project'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Popover, PopoverContent, PopoverTrigger } from '@/shared/ui/components/popover'

const RECENT_KEY = 'mohist:recent-issue-models'
const MAX_RECENT = 5
const ISSUE_STAGES = ['plan', 'build', 'check', 'integrate'] as const

interface Props {
  issueNumber: number
  currentModel?: string | null
  currentStageModels?: Record<string, string> | null
  dependencies?: IssueModelSelectorDependencies
}

export interface IssueModelSelectorDependencies {
  useAvailableModelIds: typeof useAvailableModelIds
  useModelVariants: typeof useModelVariants
  useOpencodeModel: typeof useOpencodeModel
  getIssueWorkflowVariables: typeof getIssueWorkflowVariables
  patchIssueWorkflowDefinitionVar: typeof patchIssueWorkflowDefinitionVar
  patchIssueWorkflowStageDefinitionVar: typeof patchIssueWorkflowStageDefinitionVar
}

const defaultDependencies: IssueModelSelectorDependencies = {
  useAvailableModelIds,
  useModelVariants,
  useOpencodeModel,
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

function stageModelMap(stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null): Record<string, string> {
  const result: Record<string, string> = {}
  if (!stages) return result
  for (const [stage, stageVars] of Object.entries(stages)) {
    const model = agentModel(stageVars?.vars)
    if (model) result[stage] = model
  }
  return result
}

function stageVariantMap(stages?: Record<string, { vars?: Record<string, unknown> | null } | null> | null): Record<string, string> {
  const result: Record<string, string> = {}
  if (!stages) return result
  for (const [stage, stageVars] of Object.entries(stages)) {
    const variant = agentVariant(stageVars?.vars)
    if (variant) result[stage] = variant
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
  const recent = getRecent().filter(id => id !== modelId)
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
      <path fillRule="evenodd" d="M7.21 8.145a.75.75 0 011.06-.02L10 9.835l1.73-1.71a.75.75 0 011.04 1.08l-2.25 2.22a.75.75 0 01-1.04 0l-2.25-2.22a.75.75 0 01-.02-1.06z" clipRule="evenodd" />
    </svg>
  )
}

function modelDisplayName(modelId: string): string {
  return describeModel(modelId).name
}

interface ModelListItemProps {
  modelId: string
  isSelected: boolean
  isHighlighted: boolean
  modelVariants: Record<string, string[]>
  activeVariant: string | null
  variantTestIdBase: string
  onSelect: () => void
  onSelectVariant: (variant: string) => void
  onMouseEnter: () => void
}

function ModelListItem({
  modelId,
  isSelected,
  isHighlighted,
  modelVariants,
  activeVariant,
  variantTestIdBase,
  onSelect,
  onSelectVariant,
  onMouseEnter,
}: ModelListItemProps) {
  return (
    <div
      role="button"
      tabIndex={-1}
      data-model-id={modelId}
      onClick={onSelect}
      onPointerDown={(e) => {
        if (e.target === e.currentTarget) {
          e.preventDefault()
          onSelect()
        }
      }}
      onMouseEnter={onMouseEnter}
      className={`flex w-full items-center justify-between gap-2 px-3 py-2 text-sm cursor-default ${
        isHighlighted ? 'bg-blue-50 text-blue-700' : isSelected ? 'bg-muted text-foreground' : 'text-foreground hover:bg-muted'
      }`}
    >
      <div className="flex min-w-0 flex-col items-start gap-1">
        <span className="font-medium">{modelDisplayName(modelId)}</span>
        <span className="text-xs text-muted-foreground/70">{modelId}</span>
      </div>
      <ModelVariantChips
        modelId={modelId}
        modelVariants={modelVariants}
        activeVariant={isSelected ? activeVariant : null}
        baseTestId={variantTestIdBase}
        onSelect={(_id, variant) => onSelectVariant(variant ?? '')}
      />
    </div>
  )
}

export function IssueModelSelector({ issueNumber, currentModel, currentStageModels, dependencies = defaultDependencies }: Props) {
  const {
    useAvailableModelIds,
    useModelVariants,
    useOpencodeModel,
    getIssueWorkflowVariables,
    patchIssueWorkflowDefinitionVar,
    patchIssueWorkflowStageDefinitionVar,
  } = dependencies
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { data: availableModels, isLoading, error } = useAvailableModelIds()
  const { data: opencodeModelData } = useOpencodeModel()
  const modelVariantsMap = useModelVariants()
  const [searchQuery, setSearchQuery] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})
  const [localStageVariants, setLocalStageVariants] = useState<Record<string, string>>({})
  const [localWorkflowModel, setLocalWorkflowModel] = useState<string | null>(null)
  const [localWorkflowVariant, setLocalWorkflowVariant] = useState<string | null>(null)
  const [popoverOpen, setPopoverOpen] = useState(false)

  useEffect(() => {
    let cancelled = false
    if (!projectId) {
      setLocalWorkflowModel(null)
      setLocalWorkflowVariant(null)
      setLocalStageModels(currentStageModels ?? {})
      setLocalStageVariants({})
      return
    }

    getIssueWorkflowVariables(issueNumber, projectId)
      .then((variables) => {
        if (cancelled) return
        setLocalWorkflowModel(agentModel(variables.vars))
        setLocalWorkflowVariant(agentVariant(variables.vars))
        setLocalStageModels(stageModelMap(variables.stages))
        setLocalStageVariants(stageVariantMap(variables.stages))
      })
      .catch((err) => {
        if (cancelled) return
        console.error('Failed to load issue workflow variables:', err)
        setLocalStageModels(currentStageModels ?? {})
        setLocalStageVariants({})
      })

    return () => {
      cancelled = true
    }
  }, [issueNumber, projectId, currentStageModels])

  const allModels: string[] = availableModels?.models ?? []
  const recentModelIds = getRecent()
  const recentModels = recentModelIds.filter(id => allModels.includes(id))

  const searchableModels = allModels.map(id => ({ id, display: modelDisplayName(id) }))
  const filteredResults = searchQuery.trim()
    ? fuzzysort.go(searchQuery, searchableModels, { keys: ['display', 'id'] }).map(r => r.obj.id)
    : []

  const displayedModels = searchQuery.trim() ? filteredResults : allModels

  const handleSelect = useCallback(
    async (modelId: string) => {
      try {
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowDefinitionVar(issueNumber, 'agent', { model: modelId, variant: null }, projectId)
        setLocalWorkflowModel(modelId)
        setLocalWorkflowVariant(null)
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
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
        await patchIssueWorkflowDefinitionVar(issueNumber, 'agent', { model: modelId, variant }, projectId)
        setLocalWorkflowModel(modelId)
        setLocalWorkflowVariant(variant)
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
        setPopoverOpen(false)
      } catch (err) {
        console.error('Failed to update issue model with variant:', err)
      }
    },
    [issueNumber, projectId, queryClient],
  )

  const handleClear = useCallback(
    async () => {
      try {
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowDefinitionVar(issueNumber, 'agent', { model: null, variant: null }, projectId)
        setLocalWorkflowModel(null)
        setLocalWorkflowVariant(null)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
        setPopoverOpen(false)
      } catch (err) {
        console.error('Failed to clear issue model:', err)
      }
    },
    [issueNumber, projectId, queryClient],
  )

  const handleSetStageModel = useCallback(
    async (stage: string, modelId: string) => {
      try {
        const updated = { ...localStageModels, [stage]: modelId }
        if (!projectId) throw new Error('Project is required')
        await patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { model: modelId, variant: null }, projectId)
        setLocalStageModels(updated)
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
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
        await patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { model: null, variant: null }, projectId)
        setLocalStageModels(updated)
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
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
          await patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { model: modelId, variant }, projectId)
        } else {
          await patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { model: modelId, variant: null }, projectId)
        }
        setLocalStageModels((prev) => ({ ...prev, [stage]: modelId }))
        setLocalStageVariants((prev) => {
          const next = { ...prev }
          if (variant) next[stage] = variant
          else delete next[stage]
          return next
        })
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      } catch (err) {
        console.error('Failed to update stage model variant:', err)
      }
    },
    [issueNumber, projectId, queryClient],
  )

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      if (e.key === 'ArrowDown') {
        e.preventDefault()
        setHighlightedIndex(i => Math.min(i + 1, displayedModels.length - 1))
      } else if (e.key === 'ArrowUp') {
        e.preventDefault()
        setHighlightedIndex(i => Math.max(i - 1, 0))
      } else if (e.key === 'Enter') {
        e.preventDefault()
        if (displayedModels[highlightedIndex]) {
          handleSelect(displayedModels[highlightedIndex])
        }
      }
    },
    [displayedModels, highlightedIndex, handleSelect],
  )

  useEffect(() => {
    setHighlightedIndex(0)
  }, [searchQuery])

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
          <div className="p-2">
            <div className="relative">
              <div className="absolute left-3 top-1/2 -translate-y-1/2">
                <SearchIcon />
              </div>
              <Input
                ref={searchInputRef}
                type="text"
                value={searchQuery}
                onChange={e => setSearchQuery(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Search models..."
                className="w-full pl-9"
                autoFocus
              />
            </div>
          </div>

          <div ref={listRef} className="max-h-80 overflow-y-auto border-t">
            {isLoading && (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground/70">
                Loading models...
              </div>
            )}

            {error && !isLoading && (
              <div className="px-3 py-6 text-center text-sm text-red-500">
                Failed to load models: {(error as Error).message}
              </div>
            )}

            {!isLoading && !error && configuredModel && !searchQuery.trim() && (
              <div>
                <div className="px-3 py-1.5 text-xs font-medium text-muted-foreground/70 uppercase tracking-wider bg-muted">
                  Override
                </div>
                <Button
                  variant="ghost"
                  onClick={handleClear}
                  className="w-full justify-start px-3 py-2 text-sm text-amber-700 hover:bg-amber-50 h-auto font-normal"
                >
                  <span className="font-medium">Use default{defaultModelId ? ` (${describeModel(defaultModelId).name})` : ''}</span>
                </Button>
                <div className="border-t my-1" />
              </div>
            )}

            {!isLoading && !error && recentModels.length > 0 && !searchQuery.trim() && (
              <div>
                <div className="px-3 py-1.5 text-xs font-medium text-muted-foreground/70 uppercase tracking-wider bg-muted">
                  Recent
                </div>
                {recentModels.map((modelId, i) => (
                  <ModelListItem
                    key={modelId}
                    modelId={modelId}
                    isSelected={modelId === configuredModel}
                    isHighlighted={i === highlightedIndex}
                    modelVariants={modelVariantsMap}
                    activeVariant={localWorkflowVariant}
                    variantTestIdBase={`issue-coder-model-variant-${modelId}`}
                    onSelect={() => handleSelect(modelId)}
                    onSelectVariant={(variant) => handleSelectWithVariant(modelId, variant)}
                    onMouseEnter={() => setHighlightedIndex(i)}
                  />
                ))}
                <div className="border-t my-1" />
              </div>
            )}

            {!isLoading && !error && displayedModels.length === 0 && (
              <div className="px-3 py-6 text-center text-sm text-muted-foreground/70">
                No models found
              </div>
            )}

            {!isLoading && !error && !searchQuery.trim() &&
              allModels.map((modelId, i) => (
                <ModelListItem
                  key={modelId}
                  modelId={modelId}
                  isSelected={modelId === configuredModel}
                  isHighlighted={i === highlightedIndex}
                  modelVariants={modelVariantsMap}
                  activeVariant={localWorkflowVariant}
                  variantTestIdBase={`issue-coder-model-variant-${modelId}`}
                  onSelect={() => handleSelect(modelId)}
                  onSelectVariant={(variant) => handleSelectWithVariant(modelId, variant)}
                  onMouseEnter={() => setHighlightedIndex(i)}
                />
              ))}

            {!isLoading && !error && searchQuery.trim() &&
              displayedModels.map((modelId, i) => (
                <ModelListItem
                  key={modelId}
                  modelId={modelId}
                  isSelected={modelId === configuredModel}
                  isHighlighted={i === highlightedIndex}
                  modelVariants={modelVariantsMap}
                  activeVariant={localWorkflowVariant}
                  variantTestIdBase={`issue-coder-model-variant-${modelId}`}
                  onSelect={() => handleSelect(modelId)}
                  onSelectVariant={(variant) => handleSelectWithVariant(modelId, variant)}
                  onMouseEnter={() => setHighlightedIndex(i)}
                />
              ))}
          </div>

          <div className="border-t p-2 text-xs text-muted-foreground/70 text-center">
            Use ↑↓ to navigate, Enter to select, Esc to close
          </div>
        </PopoverContent>
      </Popover>
      {configuredModel && (
        <p className="text-xs text-muted-foreground/70">
          Override active. Falls back to default when cleared.
        </p>
      )}

      <div className="pt-2 border-t">
        <Button
          variant="ghost"
          onClick={() => setAdvancedOpen(!advancedOpen)}
          className="flex items-center gap-1.5 w-full justify-start h-auto px-0 py-0 font-normal hover:bg-transparent"
        >
          <ChevronRightIcon className={`h-3.5 w-3.5 text-muted-foreground/70 transition-transform ${advancedOpen ? 'rotate-90' : ''}`} />
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
              return (
                <div key={stage} className="flex items-center gap-2">
                  <span className="text-xs font-medium text-muted-foreground w-16 capitalize shrink-0">{stage}</span>
                  <div className="flex-1">
                    <ModelSelect
                      id={`issue-stage-model-${stage}`}
                      value={stageModel}
                      placeholder="Default"
                      models={allModels}
                      onChange={(modelId) => handleSetStageModel(stage, modelId)}
                      onClear={() => handleClearStageModel(stage)}
                      allowClear={!!stageModel}
                      size="compact"
                      modelVariants={modelVariantsMap}
                      valueVariant={stageVariant}
                      onChangeModelVariant={(modelId, variant) => handleSetStageVariant(stage, modelId, variant)}
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
