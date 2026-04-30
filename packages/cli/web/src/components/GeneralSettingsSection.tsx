import { useState, useEffect, useRef, useCallback, Fragment } from 'react'
import { Popover, Transition } from '@headlessui/react'
import fuzzysort from 'fuzzysort'
import { useConfig, useUpdateConfig, useOpencodeModel, useUpdateOpencodeModel, useOpencodeModels } from '../hooks/useQueries'

const DEFAULTS = {
  agentTimeout: 1800000,
  maxConcurrentAgents: 8,
  pollInterval: 30000,
}

function msToMin(ms: number): number {
  return Math.round(ms / 60000)
}

function minToMs(min: number): number {
  return min * 60000
}

function msToSec(ms: number): number {
  return Math.round(ms / 1000)
}

function secToMs(sec: number): number {
  return sec * 1000
}

function validateTimeout(min: number): string | null {
  if (isNaN(min) || !Number.isInteger(min)) return 'Must be a whole number'
  if (min < 1) return 'Must be at least 1 minute'
  return null
}

function validateMaxConcurrent(count: number): string | null {
  if (isNaN(count) || !Number.isInteger(count)) return 'Must be a whole number'
  if (count < 1) return 'Must be at least 1'
  if (count > 16) return 'Must be at most 16'
  return null
}

function validatePollInterval(sec: number): string | null {
  if (isNaN(sec) || !Number.isInteger(sec)) return 'Must be a whole number'
  if (sec < 5) return 'Must be at least 5 seconds'
  return null
}

interface FieldConfig {
  field: string
  backendKey: string
  label: string
  unit: string
  description: string
  toDisplay: (ms: number) => number
  toMs: (display: number) => number
  validate: (v: number) => string | null
  getDefault: (c: typeof DEFAULTS) => number
}

const FIELDS: FieldConfig[] = [
  {
    field: 'timeout',
    backendKey: 'agent.timeout',
    label: 'Agent Timeout',
    unit: 'minutes',
    description: 'Maximum time an agent session can run before being terminated.',
    toDisplay: msToMin,
    toMs: minToMs,
    validate: validateTimeout,
    getDefault: (c) => c.agentTimeout,
  },
  {
    field: 'maxConcurrent',
    backendKey: 'agent.maxConcurrent',
    label: 'Max Concurrent Agents',
    unit: 'agents',
    description: 'Maximum number of agent sessions that can run simultaneously.',
    toDisplay: (ms) => ms,
    toMs: (display) => display,
    validate: validateMaxConcurrent,
    getDefault: (c) => c.maxConcurrentAgents,
  },
  {
    field: 'pollInterval',
    backendKey: 'poll.interval',
    label: 'Poll Interval',
    unit: 'seconds',
    description: 'How often the server checks for issue state changes.',
    toDisplay: msToSec,
    toMs: secToMs,
    validate: validatePollInterval,
    getDefault: (c) => c.pollInterval,
  },
]

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

function XIcon() {
  return (
    <svg className="h-3.5 w-3.5" viewBox="0 0 20 20" fill="currentColor">
      <path d="M6.28 5.22a.75.75 0 00-1.06 1.06L8.94 10l-3.72 3.72a.75.75 0 101.06 1.06L10 11.06l3.72 3.72a.75.75 0 101.06-1.06L11.06 10l3.72-3.72a.75.75 0 00-1.06-1.06L10 8.94 6.28 5.22z" />
    </svg>
  )
}

function DefaultCoderModelSelector() {
  const { data: modelData, isLoading: modelLoading } = useOpencodeModel()
  const { data: models, isLoading: modelsLoading } = useOpencodeModels()
  const updateModel = useUpdateOpencodeModel()

  const [searchQuery, setSearchQuery] = useState('')
  const [highlightedIndex, setHighlightedIndex] = useState(0)
  const [error, setError] = useState<string | null>(null)
  const searchInputRef = useRef<HTMLInputElement>(null)

  const isLoading = modelLoading || modelsLoading
  const currentModel = modelData?.model ?? null

  const filteredModels = searchQuery.trim() && models
    ? fuzzysort.go(searchQuery, models).map(r => r.target)
    : models ?? []

  const displayedModels = searchQuery.trim() ? filteredModels : (models ?? [])

  useEffect(() => {
    setHighlightedIndex(0)
  }, [searchQuery])

  const handleSelect = useCallback(
    async (modelId: string) => {
      setError(null)
      try {
        await updateModel.mutateAsync(modelId)
      } catch (err) {
        setError((err as Error).message || 'Failed to update model')
      }
    },
    [updateModel],
  )

  const handleClear = useCallback(
    async () => {
      setError(null)
      try {
        await updateModel.mutateAsync(null)
      } catch (err) {
        setError((err as Error).message || 'Failed to clear model')
      }
    },
    [updateModel],
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

  const displayText = currentModel
    ? currentModel.split('/').pop() || currentModel
    : 'opencode default'

  if (isLoading) {
    return (
      <div className="space-y-1.5">
        <div className="h-4 w-36 bg-gray-100 rounded animate-pulse" />
        <div className="h-9 w-full bg-gray-100 rounded-md animate-pulse" />
      </div>
    )
  }

  return (
    <div className="space-y-1.5">
      <label className="block text-sm font-medium text-gray-700">Default Coder Model</label>
      <p className="text-xs text-gray-500">
        Default model for coder agent sessions. Falls back to opencode&apos;s internal default when not set.
      </p>
      <Popover as="div" className="relative">
        {({ open }) => (
          <>
            <div className="flex items-center gap-2">
              <Popover.Button
                className={`flex-1 inline-flex items-center justify-between gap-1.5 rounded-md border px-3 py-1.5 text-sm font-medium transition-colors shadow-sm min-h-[36px] ${
                  open
                    ? 'border-blue-500 bg-blue-50 text-blue-700'
                    : currentModel
                      ? 'border-blue-200 bg-blue-50 text-blue-700'
                      : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
                }`}
              >
                <span className="truncate">{displayText}</span>
                <ChevronDownIcon />
              </Popover.Button>
              {currentModel && (
                <button
                  onClick={(e) => {
                    e.stopPropagation()
                    handleClear()
                  }}
                  disabled={updateModel.isPending}
                  className="inline-flex items-center justify-center w-8 h-8 rounded-md border border-gray-300 text-gray-400 hover:text-gray-600 hover:bg-gray-50 transition-colors"
                  title="Clear model"
                >
                  <XIcon />
                </button>
              )}
            </div>

            <Transition
              as={Fragment}
              enter="transition ease-out duration-100"
              enterFrom="transform opacity-0 scale-95"
              enterTo="transform opacity-100 scale-100"
              leave="transition ease-in duration-75"
              leaveFrom="transform opacity-100 scale-100"
              leaveTo="transform opacity-0 scale-95"
            >
              <Popover.Panel className="absolute right-0 z-50 mt-2 w-72 origin-top-right rounded-lg bg-white shadow-lg ring-1 ring-black/5 focus:outline-none">
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

                <div className="max-h-64 overflow-y-auto border-t border-gray-100">
                  {displayedModels.length === 0 && (
                    <div className="px-3 py-6 text-center text-sm text-gray-400">
                      {models && models.length === 0 ? 'No models available' : 'No models found'}
                    </div>
                  )}

                  {displayedModels.map((modelId, i) => (
                    <button
                      key={modelId}
                      onClick={() => handleSelect(modelId)}
                      onMouseEnter={() => setHighlightedIndex(i)}
                      className={`w-full flex items-center justify-between px-3 py-2 text-sm transition-colors ${
                        i === highlightedIndex ? 'bg-blue-50 text-blue-700' : modelId === currentModel ? 'bg-gray-50 text-gray-900' : 'text-gray-700 hover:bg-gray-50'
                      }`}
                    >
                      <span className="font-medium">{modelId.split('/').pop()}</span>
                      <span className="text-xs text-gray-400">{modelId}</span>
                    </button>
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
      {error && <p className="text-xs text-red-600">{error}</p>}
    </div>
  )
}

export function GeneralSettingsSection() {
  const { data: config, isLoading, error, refetch } = useConfig()
  const updateConfig = useUpdateConfig()

  const [values, setValues] = useState<Record<string, number>>({
    timeout: 30,
    maxConcurrent: 8,
    pollInterval: 30,
  })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState<Record<string, boolean>>({})

  useEffect(() => {
    if (config) {
      setValues({
        timeout: msToMin(config.agentTimeout),
        maxConcurrent: config.maxConcurrentAgents,
        pollInterval: msToSec(config.pollInterval),
      })
    }
  }, [config])

  const clearError = (field: string) => {
    setErrors((prev) => {
      const next = { ...prev }
      delete next[field]
      return next
    })
  }

  const handleSave = async (fc: FieldConfig) => {
    const displayValue = values[fc.field]
    const validationError = fc.validate(displayValue)
    if (validationError) {
      setErrors((prev) => ({ ...prev, [fc.field]: validationError }))
      return
    }

    clearError(fc.field)
    setSaving((prev) => ({ ...prev, [fc.field]: true }))

    try {
      await updateConfig.mutateAsync({ key: fc.backendKey, value: fc.toMs(displayValue) })
    } catch (err) {
      if (config) {
        setValues((prev) => ({
          ...prev,
          [fc.field]: fc.toDisplay(fc.getDefault(config as unknown as typeof DEFAULTS)),
        }))
      }
      setErrors((prev) => ({ ...prev, [fc.field]: (err as Error).message || 'Save failed' }))
    } finally {
      setSaving((prev) => ({ ...prev, [fc.field]: false }))
    }
  }

  const handleReset = async () => {
    if (!window.confirm('Reset all settings to defaults?')) return

    clearError('reset')
    try {
      await Promise.all([
        updateConfig.mutateAsync({ key: 'agent.timeout', value: DEFAULTS.agentTimeout }),
        updateConfig.mutateAsync({ key: 'agent.maxConcurrent', value: DEFAULTS.maxConcurrentAgents }),
        updateConfig.mutateAsync({ key: 'poll.interval', value: DEFAULTS.pollInterval }),
      ])
    } catch (err) {
      setErrors((prev) => ({ ...prev, reset: (err as Error).message || 'Reset failed' }))
    }
  }

  if (isLoading) {
    return (
      <div className="space-y-6">
        <h3 className="text-sm font-medium text-gray-900">General Settings</h3>
        <div className="space-y-6">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="space-y-1.5">
              <div className="h-4 w-32 bg-gray-100 rounded animate-pulse" />
              <div className="h-9 w-full bg-gray-100 rounded-md animate-pulse" />
            </div>
          ))}
        </div>
      </div>
    )
  }

  if (error) {
    return (
      <div className="space-y-4">
        <h3 className="text-sm font-medium text-gray-900">General Settings</h3>
        <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
          Failed to load settings: {error.message}
        </div>
        <button
          onClick={() => refetch()}
          className="px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 rounded-md transition-colors"
        >
          Retry
        </button>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <h3 className="text-sm font-medium text-gray-900">General Settings</h3>

      {FIELDS.map((fc) => (
        <div key={fc.field} className="space-y-1.5">
          <label className="block text-sm font-medium text-gray-700">{fc.label}</label>
          <div className="flex items-center gap-2">
            <input
              type="number"
              min={fc.field === 'timeout' ? 1 : fc.field === 'maxConcurrent' ? 1 : 5}
              max={fc.field === 'maxConcurrent' ? 16 : undefined}
              value={values[fc.field]}
              onChange={(e) => {
                const v = parseInt(e.target.value, 10)
                setValues((prev) => ({ ...prev, [fc.field]: isNaN(v) ? 0 : v }))
                clearError(fc.field)
              }}
              className="w-24 px-3 py-1.5 border border-gray-300 rounded-md text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
            />
            <span className="text-sm text-gray-500">{fc.unit}</span>
            <button
              onClick={() => handleSave(fc)}
              disabled={saving[fc.field]}
              className="ml-auto px-3 py-1.5 text-sm font-medium text-white bg-blue-600 hover:bg-blue-700 disabled:opacity-50 rounded-md transition-colors"
            >
              {saving[fc.field] ? 'Saving...' : 'Save'}
            </button>
          </div>
          {errors[fc.field] && <p className="text-xs text-red-600">{errors[fc.field]}</p>}
          <p className="text-xs text-gray-500">{fc.description}</p>
        </div>
      ))}

      <DefaultCoderModelSelector />

      <hr className="border-gray-100" />

      {errors.reset && <p className="text-xs text-red-600">{errors.reset}</p>}
      <button
        onClick={handleReset}
        className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 hover:bg-gray-50 rounded-md transition-colors"
      >
        Reset to Defaults
      </button>
    </div>
  )
}
