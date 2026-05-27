import React, { useState, useEffect, useCallback, useRef, Fragment } from 'react'
import { Popover, Transition } from '@headlessui/react'
import fuzzysort from 'fuzzysort'
import { useAvailableModelIds, useOpencodeModel } from '../hooks/useQueries'
import { api } from '../lib/api'
import { useQueryClient } from '@tanstack/react-query'
import { ModelSelect } from './ModelSelect'
import { useProject } from '../context/ProjectContext'

const RECENT_KEY = 'mohist:recent-issue-models'
const MAX_RECENT = 5
const ISSUE_STAGES = ['plan', 'build', 'check', 'integrate'] as const

interface Props {
  issueNumber: number
  currentWorkflowRunId?: string | null
  currentModel?: string | null
  currentAgentConfig?: Record<string, unknown> | null
  currentStageModels?: Record<string, string> | null
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
    <svg className="h-4 w-4 text-gray-400" viewBox="0 0 20 20" fill="currentColor">
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
  return modelId.split('/').pop() || modelId
}

interface ModelListItemProps {
  modelId: string
  isSelected: boolean
  isHighlighted: boolean
  onSelect: () => void
  onMouseEnter: () => void
}

function ModelListItem({ modelId, isSelected, isHighlighted, onSelect, onMouseEnter }: ModelListItemProps) {
  return (
    <button
      onClick={onSelect}
      onMouseEnter={onMouseEnter}
      className={`w-full flex items-center justify-between px-3 py-2 text-sm transition-colors ${
        isHighlighted ? 'bg-blue-50 text-blue-700' : isSelected ? 'bg-gray-50 text-gray-900' : 'text-gray-700 hover:bg-gray-50'
      }`}
    >
      <div className="flex flex-col items-start gap-1">
        <span className="font-medium">{modelDisplayName(modelId)}</span>
        <span className="text-xs text-gray-400">{modelId}</span>
      </div>
    </button>
  )
}

export function IssueModelSelector({ issueNumber, currentWorkflowRunId, currentModel, currentAgentConfig, currentStageModels }: Props) {
  const queryClient = useQueryClient()
  const { projectId } = useProject()
  const { data: availableModelIds, isLoading, error } = useAvailableModelIds()
  const { data: opencodeModelData } = useOpencodeModel()
  const [searchQuery, setSearchQuery] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const searchInputRef = useRef<HTMLInputElement>(null)
  const listRef = useRef<HTMLDivElement>(null)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [localStageModels, setLocalStageModels] = useState<Record<string, string>>({})
  const [localWorkflowModel, setLocalWorkflowModel] = useState<string | null>(null)

  useEffect(() => {
    if (currentStageModels) {
      setLocalStageModels(currentStageModels)
    } else {
      setLocalStageModels({})
    }
  }, [currentStageModels])

  const allModels: string[] = availableModelIds ?? []
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
        if (currentWorkflowRunId) {
          await api.patchIssueWorkflowDefinitionVar(issueNumber, 'agent', { type: 'opencode', model: modelId }, projectId)
          setLocalWorkflowModel(modelId)
        } else {
          await api.updateIssue(issueNumber, { agentConfig: { ...(currentAgentConfig ?? {}), model: modelId } }, projectId)
        }
        addRecent(modelId)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      } catch (err) {
        console.error('Failed to update issue model:', err)
      }
    },
    [issueNumber, currentWorkflowRunId, currentAgentConfig, projectId, queryClient],
  )

  const handleClear = useCallback(
    async () => {
      try {
        if (currentWorkflowRunId) {
          await api.patchIssueWorkflowDefinitionVar(issueNumber, 'agent', { model: null }, projectId)
          setLocalWorkflowModel(null)
        } else {
          const updatedAgent = { ...(currentAgentConfig ?? {}) }
          delete updatedAgent.model
          await api.updateIssue(issueNumber, { model: null, agentConfig: Object.keys(updatedAgent).length > 0 ? updatedAgent : null }, projectId)
        }
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      } catch (err) {
        console.error('Failed to clear issue model:', err)
      }
    },
    [issueNumber, currentWorkflowRunId, currentAgentConfig, projectId, queryClient],
  )

  const handleSetStageModel = useCallback(
    async (stage: string, modelId: string) => {
      try {
        const updated = { ...localStageModels, [stage]: modelId }
        if (currentWorkflowRunId) {
          await api.patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { type: 'opencode', model: modelId }, projectId)
        } else {
          await api.updateIssue(issueNumber, { stageModels: updated }, projectId)
        }
        setLocalStageModels(updated)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      } catch (err) {
        console.error('Failed to update stage model:', err)
      }
    },
    [issueNumber, currentWorkflowRunId, localStageModels, projectId, queryClient],
  )

  const handleClearStageModel = useCallback(
    async (stage: string) => {
      try {
        const updated = { ...localStageModels }
        delete updated[stage]
        if (currentWorkflowRunId) {
          await api.patchIssueWorkflowStageDefinitionVar(issueNumber, stage, 'agent', { model: null }, projectId)
        } else {
          const stageModelsValue = Object.keys(updated).length > 0 ? updated : null
          await api.updateIssue(issueNumber, { stageModels: stageModelsValue }, projectId)
        }
        setLocalStageModels(updated)
        queryClient.invalidateQueries({ queryKey: ['issues', issueNumber] })
        queryClient.invalidateQueries({ queryKey: ['issues'] })
      } catch (err) {
        console.error('Failed to clear stage model:', err)
      }
    },
    [issueNumber, currentWorkflowRunId, localStageModels, projectId, queryClient],
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

  const defaultModelName = opencodeModelData?.model
    ? opencodeModelData.model.split('/').pop()!
    : null

  const configuredModel = localWorkflowModel ?? (typeof currentAgentConfig?.model === 'string' ? currentAgentConfig.model : currentModel)

  const currentModelDisplay = configuredModel
    ? modelDisplayName(configuredModel)
    : defaultModelName || 'Use default'

  return (
    <div className="space-y-1">
      <label className="block text-sm text-gray-500">Coder Model</label>
      <Popover as="div" className="relative">
        {({ open }) => (
          <>
            <Popover.Button
              className={`w-full inline-flex items-center justify-between gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors shadow-sm min-h-[44px] md:min-h-0 ${
                open
                  ? 'border-blue-500 bg-blue-50 text-blue-700'
                    : configuredModel
                    ? 'border-blue-200 bg-blue-50 text-blue-700'
                    : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
              }`}
            >
              <span className="truncate">{currentModelDisplay}</span>
              <ChevronDownIcon />
            </Popover.Button>

            <Transition
              as={Fragment}
              enter="transition ease-out duration-100"
              enterFrom="transform opacity-0 scale-95"
              enterTo="transform opacity-100 scale-100"
              leave="transition ease-in duration-75"
              leaveFrom="transform opacity-100 scale-100"
              leaveTo="transform opacity-0 scale-95"
            >
              <Popover.Panel portal={false} className="fixed inset-x-2 top-auto z-50 mt-2 md:absolute md:inset-x-auto md:right-0 md:w-80 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none">
                <div className="p-2">
                  <div className="relative">
                    <div className="absolute left-3 top-1/2 -translate-y-1/2">
                      <SearchIcon />
                    </div>
                    <input
                      ref={searchInputRef}
                      type="text"
                      value={searchQuery}
                      onChange={e => setSearchQuery(e.target.value)}
                      onKeyDown={handleKeyDown}
                      placeholder="Search models..."
                      className="w-full rounded-md border border-gray-300 pl-9 pr-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                      autoFocus
                    />
                  </div>
                </div>

                <div ref={listRef} className="max-h-80 overflow-y-auto border-t border-gray-100">
                  {isLoading && (
                    <div className="px-3 py-6 text-center text-sm text-gray-400">
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
                      <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50">
                        Override
                      </div>
                      <button
                        onClick={handleClear}
                        className="w-full flex items-center px-3 py-2 text-sm text-amber-700 hover:bg-amber-50 transition-colors"
                      >
                        <span className="font-medium">Use default{defaultModelName ? ` (${defaultModelName})` : ''}</span>
                      </button>
                      <div className="border-t border-gray-100 my-1" />
                    </div>
                  )}

                  {!isLoading && !error && recentModels.length > 0 && !searchQuery.trim() && (
                    <div>
                      <div className="px-3 py-1.5 text-xs font-medium text-gray-400 uppercase tracking-wider bg-gray-50">
                        Recent
                      </div>
                      {recentModels.map((modelId, i) => (
                        <ModelListItem
                          key={modelId}
                          modelId={modelId}
                          isSelected={modelId === configuredModel}
                          isHighlighted={i === highlightedIndex}
                          onSelect={() => handleSelect(modelId)}
                          onMouseEnter={() => setHighlightedIndex(i)}
                        />
                      ))}
                      <div className="border-t border-gray-100 my-1" />
                    </div>
                  )}

                  {!isLoading && !error && displayedModels.length === 0 && (
                    <div className="px-3 py-6 text-center text-sm text-gray-400">
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
                        onSelect={() => handleSelect(modelId)}
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
                        onSelect={() => handleSelect(modelId)}
                        onMouseEnter={() => setHighlightedIndex(i)}
                      />
                    ))}
                </div>

                <div className="border-t border-gray-100 p-2 text-xs text-gray-400 text-center">
                  Use ↑↓ to navigate, Enter to select, Esc to close
                </div>
              </Popover.Panel>
            </Transition>
          </>
        )}
      </Popover>
      {configuredModel && (
        <p className="text-xs text-gray-400">
          Override active. Falls back to default when cleared.
        </p>
      )}

      <div className="pt-2 border-t border-gray-100">
        <button
          onClick={() => setAdvancedOpen(!advancedOpen)}
          className="flex items-center gap-1.5 w-full text-left"
        >
          <ChevronRightIcon className={`h-3.5 w-3.5 text-gray-400 transition-transform ${advancedOpen ? 'rotate-90' : ''}`} />
          <span className="text-xs text-gray-500">Per-stage overrides</span>
          {Object.keys(localStageModels).length > 0 && (
            <span className="text-xs text-blue-500">({Object.keys(localStageModels).length})</span>
          )}
        </button>

        {advancedOpen && (
          <div className="mt-3 space-y-2 pl-5">
            {ISSUE_STAGES.map((stage) => (
              <div key={stage} className="flex items-center gap-2">
                <span className="text-xs font-medium text-gray-500 w-16 capitalize shrink-0">{stage}</span>
                <div className="flex-1 flex items-center gap-1">
                  <ModelSelect
                    value={localStageModels[stage] ?? null}
                    placeholder="Default"
                    models={allModels}
                    onChange={(modelId) => handleSetStageModel(stage, modelId)}
                    onClear={() => handleClearStageModel(stage)}
                    allowClear={!!localStageModels[stage]}
                    size="compact"
                  />
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
