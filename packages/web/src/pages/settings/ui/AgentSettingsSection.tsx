import { useState, useEffect, useMemo } from 'react'
import { useAgentRuntime, useConfig, useSetAgentRuntime } from '../../../entities/settings'
import type { AgentRuntimeConfig, GeneralConfig } from '../../../entities/settings'
import { agentRuntimeToConfigKey } from '../../../entities/settings/api/client'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { SectionState } from './SectionState'

const DEFAULTS: AgentRuntimeConfig = {
  timeout: 600000,
  stageTimeout: 3600000,
  taskTimeout: 600000,
  maxConcurrent: 3,
  maxGracePeriods: 3,
  pollInterval: 5000,
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

interface FormValues {
  timeout: number
  stageTimeout: number
  taskTimeout: number
  maxConcurrent: number
  maxGracePeriods: number
  pollInterval: number
}

function configToForm(c: AgentRuntimeConfig): FormValues {
  return {
    timeout: msToMin(c.timeout),
    stageTimeout: msToMin(c.stageTimeout),
    taskTimeout: msToMin(c.taskTimeout),
    maxConcurrent: c.maxConcurrent,
    maxGracePeriods: c.maxGracePeriods,
    pollInterval: msToSec(c.pollInterval),
  }
}

function formToConfig(f: FormValues): AgentRuntimeConfig {
  return {
    timeout: minToMs(f.timeout),
    stageTimeout: minToMs(f.stageTimeout),
    taskTimeout: minToMs(f.taskTimeout),
    maxConcurrent: f.maxConcurrent,
    maxGracePeriods: f.maxGracePeriods,
    pollInterval: secToMs(f.pollInterval),
  }
}

function validateTimeout(val: number): string | null {
  if (isNaN(val) || !Number.isInteger(val)) return 'Must be a whole number'
  if (val < 1) return 'Must be at least 1 minute'
  return null
}

function validateMaxConcurrent(val: number): string | null {
  if (isNaN(val) || !Number.isInteger(val)) return 'Must be a whole number'
  if (val < 1) return 'Must be at least 1'
  if (val > 16) return 'Must be at most 16'
  return null
}

function validatePollInterval(val: number): string | null {
  if (isNaN(val) || !Number.isInteger(val)) return 'Must be a whole number'
  if (val < 5) return 'Must be at least 5 seconds'
  return null
}

function validateGracePeriods(val: number): string | null {
  if (isNaN(val) || !Number.isInteger(val)) return 'Must be a whole number'
  if (val < 0) return 'Must be 0 or greater'
  return null
}

type FieldKey = keyof FormValues

interface FieldDef {
  key: FieldKey
  label: string
  unit: string
  description: string
  validate: (v: number) => string | null
  group: 'timeout' | 'concurrency' | 'recovery'
}

const FIELDS: FieldDef[] = [
  {
    key: 'timeout',
    label: 'Session Timeout',
    unit: 'minutes',
    description: 'Maximum total time an external coder agent session can run.',
    validate: validateTimeout,
    group: 'timeout',
  },
  {
    key: 'stageTimeout',
    label: 'Stage Timeout',
    unit: 'minutes',
    description: 'Maximum time a single workflow stage can take.',
    validate: validateTimeout,
    group: 'timeout',
  },
  {
    key: 'taskTimeout',
    label: 'Task Timeout',
    unit: 'minutes',
    description: 'Maximum time a single task within a stage can take.',
    validate: validateTimeout,
    group: 'timeout',
  },
  {
    key: 'maxConcurrent',
    label: 'Max Concurrent',
    unit: 'sessions',
    description: 'Maximum number of external coder agent sessions running simultaneously.',
    validate: validateMaxConcurrent,
    group: 'concurrency',
  },
  {
    key: 'pollInterval',
    label: 'Poll Interval',
    unit: 'seconds',
    description: 'How often the server checks for issue state changes.',
    validate: validatePollInterval,
    group: 'concurrency',
  },
  {
    key: 'maxGracePeriods',
    label: 'Retry Budget',
    unit: 'grace periods',
    description: 'Maximum retry attempts after an external coder agent failure.',
    validate: validateGracePeriods,
    group: 'recovery',
  },
]

function TimeoutDiagram({ session, stage, task }: { session: number; stage: number; task: number }) {
  const lines = [
    'Session is the total time budget. Stage and Task are independent',
    'per-level caps, but both consume from the Session budget.',
    '',
    `Session (${session} min)`,
    '  \u251C\u2500\u2500 Stage \u2264 ' + stage + ' min',
    '  \u2502   \u2514\u2500\u2500 Task \u2264 ' + task + ' min',
    '  \u2514\u2500\u2500 Stage \u2264 ' + stage + ' min',
  ]

  return (
    <div className="rounded-md bg-muted border px-4 py-3">
      <pre className="text-xs text-muted-foreground font-mono leading-5 whitespace-pre">{lines.join('\n')}</pre>
    </div>
  )
}

function InputField({
  label,
  unit,
  value,
  error,
  disabled,
  disabledReason,
  onChange,
}: {
  label: string
  unit: string
  value: number
  error: string | null
  disabled?: boolean
  disabledReason?: string
  onChange: (v: number) => void
}) {
  return (
    <div className="space-y-1">
      <label className="block text-xs font-medium text-foreground/80">{label}</label>
      <div className="flex items-center gap-2">
        <Input
          type="number"
          value={value}
          disabled={disabled}
          onChange={(e) => {
            const v = parseInt(e.target.value, 10)
            onChange(isNaN(v) ? 0 : v)
          }}
          className="w-24"
        />
        <span className="text-sm text-muted-foreground">{unit}</span>
      </div>
      {disabled && disabledReason && (
        <p className="text-xs text-muted-foreground">{disabledReason}</p>
      )}
      {!disabled && error && <p className="text-xs text-red-600">{error}</p>}
    </div>
  )
}

export function AgentSettingsSection() {
  const { data: runtimeConfig, isLoading, error, refetch } = useAgentRuntime()
  const { data: config } = useConfig()
  const setAgentRuntime = useSetAgentRuntime()

  const [localValues, setLocalValues] = useState<FormValues>(() => configToForm(DEFAULTS))
  const [savedValues, setSavedValues] = useState<FormValues>(() => configToForm(DEFAULTS))
  const [validationErrors, setValidationErrors] = useState<Partial<Record<FieldKey, string>>>({})
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [showResetConfirm, setShowResetConfirm] = useState(false)

  const unsupportedFields = useMemo<Set<FieldKey>>(() => {
    const set = new Set<FieldKey>()
    if (!config) return set
    for (const field of FIELDS) {
      const configKey = agentRuntimeToConfigKey(field.key) as keyof GeneralConfig
      const value = config[configKey]
      if (value === undefined || value === null) {
        set.add(field.key)
      }
    }
    return set
  }, [config])

  useEffect(() => {
    if (runtimeConfig) {
      const form = configToForm(runtimeConfig)
      setLocalValues(form)
      setSavedValues(form)
    }
  }, [runtimeConfig])

  const dirty = useMemo(() => {
    return (Object.keys(localValues) as FieldKey[]).some(
      (k) => !unsupportedFields.has(k) && localValues[k] !== savedValues[k],
    )
  }, [localValues, savedValues, unsupportedFields])

  const hasValidationErrors = Object.keys(validationErrors).length > 0

  const handleChange = (key: FieldKey, value: number) => {
    setLocalValues((prev) => ({ ...prev, [key]: value }))

    const field = FIELDS.find((f) => f.key === key)
    if (field) {
      const err = field.validate(value)
      setValidationErrors((prev) => {
        const next = { ...prev }
        if (err) {
          next[key] = err
        } else {
          delete next[key]
        }
        return next
      })
    }
  }

  const handleSave = async () => {
    setSaveError(null)
    const errors: Partial<Record<FieldKey, string>> = {}
    for (const field of FIELDS) {
      if (unsupportedFields.has(field.key)) continue
      const err = field.validate(localValues[field.key])
      if (err) errors[field.key] = err
    }
    if (Object.keys(errors).length > 0) {
      setValidationErrors(errors)
      return
    }

    setSaving(true)

    try {
      const changed: Partial<AgentRuntimeConfig> = {}
      for (const key of Object.keys(localValues) as FieldKey[]) {
        if (unsupportedFields.has(key)) continue
        if (localValues[key] !== savedValues[key]) {
          const config = formToConfig(localValues)
          changed[key] = config[key]
        }
      }

      if (Object.keys(changed).length === 0) {
        setSaving(false)
        return
      }

      const result = await setAgentRuntime.mutateAsync(changed)
      setSavedValues(configToForm(result))
    } catch {
    } finally {
      setSaving(false)
    }
  }

  const handleReset = () => {
    setShowResetConfirm(true)
  }

  const confirmReset = async () => {
    setShowResetConfirm(false)
    setSaving(true)
    setSaveError(null)

    try {
      const resetPayload: Partial<AgentRuntimeConfig> = {}
      for (const field of FIELDS) {
        if (unsupportedFields.has(field.key)) continue
        resetPayload[field.key] = DEFAULTS[field.key]
      }

      if (Object.keys(resetPayload).length === 0) {
        setSaveError('No supported runtime fields to reset.')
        setSaving(false)
        return
      }

      const result = await setAgentRuntime.mutateAsync(resetPayload)
      const form = configToForm(result)
      setLocalValues(form)
      setSavedValues(form)
      setValidationErrors({})
    } catch {
    } finally {
      setSaving(false)
    }
  }

  if (isLoading) {
    return (
      <SectionState
        variant="loading"
        title="Coder Agent Runtime"
        description="Configure how Mohist schedules external coder agent sessions."
        skeletonRows={6}
      />
    )
  }

  if (error) {
    return (
      <SectionState
        variant="error"
        title="Coder Agent Runtime"
        description="Configure how Mohist schedules external coder agent sessions."
        message={`Failed to load settings: ${error.message}`}
        onRetry={() => refetch()}
      />
    )
  }

  return (
    <div className="space-y-8">
      <div>
        <h3 className="text-sm font-medium text-foreground">Coder Agent Runtime</h3>
        <p className="text-xs text-muted-foreground mt-1">Configure how Mohist schedules external coder agent sessions.</p>
      </div>

      <div className="space-y-4">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Timeouts</h4>
        <TimeoutDiagram
          session={localValues.timeout}
          stage={localValues.stageTimeout}
          task={localValues.taskTimeout}
        />
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-4">
          <InputField
            label="Session Timeout"
            unit="minutes"
            value={localValues.timeout}
            error={validationErrors.timeout ?? null}
            disabled={unsupportedFields.has('timeout')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('timeout', v)}
          />
          <InputField
            label="Stage Timeout"
            unit="minutes"
            value={localValues.stageTimeout}
            error={validationErrors.stageTimeout ?? null}
            disabled={unsupportedFields.has('stageTimeout')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('stageTimeout', v)}
          />
          <InputField
            label="Task Timeout"
            unit="minutes"
            value={localValues.taskTimeout}
            error={validationErrors.taskTimeout ?? null}
            disabled={unsupportedFields.has('taskTimeout')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('taskTimeout', v)}
          />
        </div>
      </div>

      <hr className="border" />

      <div className="space-y-4">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Concurrency</h4>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <InputField
            label="Max Concurrent"
            unit="agents"
            value={localValues.maxConcurrent}
            error={validationErrors.maxConcurrent ?? null}
            disabled={unsupportedFields.has('maxConcurrent')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('maxConcurrent', v)}
          />
          <InputField
            label="Poll Interval"
            unit="seconds"
            value={localValues.pollInterval}
            error={validationErrors.pollInterval ?? null}
            disabled={unsupportedFields.has('pollInterval')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('pollInterval', v)}
          />
        </div>
      </div>

      <hr className="border" />

      <div className="space-y-4">
        <h4 className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">Recovery</h4>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <InputField
            label="Retry Budget"
            unit="grace periods"
            value={localValues.maxGracePeriods}
            error={validationErrors.maxGracePeriods ?? null}
            disabled={unsupportedFields.has('maxGracePeriods')}
            disabledReason="This runtime field is not exposed by the server configuration."
            onChange={(v) => handleChange('maxGracePeriods', v)}
          />
        </div>
        <p className="text-xs text-muted-foreground">
          Number of times an external coder agent can fail and be retried before the issue is marked as interrupted.
        </p>
      </div>

      <hr className="border" />

      <div className="flex items-center gap-3">
        <Button
          onClick={handleSave}
          disabled={!dirty || hasValidationErrors || saving}
          className={dirty && !hasValidationErrors && !saving ? 'bg-blue-600 hover:bg-blue-700 text-white' : ''}
        >
          {saving ? 'Saving...' : 'Save Changes'}
        </Button>
        <Button
          variant="outline"
          onClick={handleReset}
          disabled={saving}
        >
          Reset to Defaults
        </Button>
      </div>

      {saveError && (
        <p className="text-xs text-red-600">{saveError}</p>
      )}

      {showResetConfirm && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50">
          <div className="bg-background rounded-lg shadow-xl p-6 w-full max-w-sm mx-4">
            <h4 className="text-lg font-medium text-foreground mb-2">Reset Coder Agent Settings</h4>
            <p className="text-sm text-muted-foreground mb-4">
              Reset all agent runtime settings to their default values?
            </p>
            <div className="flex justify-end gap-2">
              <Button
                variant="ghost"
                onClick={() => setShowResetConfirm(false)}
              >
                Cancel
              </Button>
              <Button
                onClick={confirmReset}
                disabled={saving}
                className="bg-red-600 hover:bg-red-700 text-white"
              >
                {saving ? 'Resetting...' : 'Reset'}
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
