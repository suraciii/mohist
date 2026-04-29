import { useState, useEffect } from 'react'
import { useConfig, useUpdateConfig } from '../hooks/useQueries'

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
          {[1, 2, 3].map((i) => (
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
