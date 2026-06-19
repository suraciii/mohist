import { useState, useCallback, useMemo } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import {
  formatLabelToken,
  validateLabelEntry,
  type LabelMap,
} from '../model/labels'

export interface LabelEditorProps {
  value: LabelMap
  onChange: (next: LabelMap) => void
  disabled?: boolean
  emptyHint?: string
  inputIdPrefix?: string
  'data-testid'?: string
}

interface DraftEntry {
  key: string
  value: string
}

function buildDraftList(value: LabelMap): DraftEntry[] {
  return Object.keys(value).sort().map((key) => ({
    key,
    value: value[key],
  }))
}

export function LabelEditor({
  value,
  onChange,
  disabled = false,
  emptyHint = 'No labels yet — add a key+value pair to start.',
  inputIdPrefix = 'label-editor',
  'data-testid': testId = 'label-editor',
}: LabelEditorProps) {
  const entries = useMemo(() => buildDraftList(value), [value])
  const [draftKey, setDraftKey] = useState('')
  const [draftValue, setDraftValue] = useState('')
  const [draftError, setDraftError] = useState<string | null>(null)
  const [editingKey, setEditingKey] = useState<string | null>(null)

  const commitDraft = useCallback(() => {
    const result = validateLabelEntry({ key: draftKey, value: draftValue })
    if (!result.ok) {
      setDraftError(result.error)
      return false
    }
    setDraftError(null)
    const next: LabelMap = { ...value }
    if (editingKey && editingKey !== result.entry.key) {
      delete next[editingKey]
    }
    next[result.entry.key] = result.entry.value
    onChange(next)
    setEditingKey(null)
    setDraftKey('')
    setDraftValue('')
    return true
  }, [draftKey, draftValue, editingKey, onChange, value])

  const handleSubmit = useCallback(
    (event: React.FormEvent) => {
      event.preventDefault()
      commitDraft()
    },
    [commitDraft],
  )

  const handleDraftKeyChange = useCallback(
    (next: string) => {
      setDraftKey(next)
      if (draftError) {
        const result = validateLabelEntry({ key: next, value: draftValue })
        setDraftError(result.ok ? null : result.error)
      }
    },
    [draftError, draftValue],
  )

  const handleDraftValueChange = useCallback(
    (next: string) => {
      setDraftValue(next)
      if (draftError) {
        const result = validateLabelEntry({ key: draftKey, value: next })
        setDraftError(result.ok ? null : result.error)
      }
    },
    [draftError, draftKey],
  )

  const removeEntry = useCallback(
    (key: string) => {
      const next: LabelMap = { ...value }
      delete next[key]
      onChange(next)
    },
    [onChange, value],
  )

  const startEditEntry = useCallback((entry: DraftEntry) => {
    setEditingKey(entry.key)
    setDraftKey(entry.key)
    setDraftValue(entry.value)
    setDraftError(null)
  }, [])

  const cancelEdit = useCallback(() => {
    setEditingKey(null)
    setDraftKey('')
    setDraftValue('')
    setDraftError(null)
  }, [])

  const submitLabel = editingKey ? 'Update' : 'Add label'

  return (
    <div className="space-y-2" data-testid={testId}>
      <div className="flex flex-wrap gap-1.5" data-testid="label-editor-entries">
        {entries.length === 0 ? (
          <div
            data-testid="label-editor-empty"
            className="text-xs text-muted-foreground/70 py-1"
          >
            {emptyHint}
          </div>
        ) : (
          entries.map((entry) => {
            const token = formatLabelToken(entry.key, entry.value)
            const isEditing = editingKey === entry.key
            return (
              <div
                key={entry.key}
                data-testid={`label-editor-entry-${entry.key}`}
                className="inline-flex items-center gap-1 rounded-full bg-blue-100 text-blue-700 px-2 py-0.5 text-xs"
              >
                <span className="font-medium">{token}</span>
                {!isEditing && !disabled && (
                  <button
                    type="button"
                    data-testid={`label-editor-edit-${entry.key}`}
                    onClick={() => startEditEntry(entry)}
                    className="ml-1 rounded-full px-1 text-[10px] font-semibold hover:bg-blue-200"
                    aria-label={`Edit label ${token}`}
                  >
                    Edit
                  </button>
                )}
                {!isEditing && !disabled && (
                  <button
                    type="button"
                    data-testid={`label-editor-remove-${entry.key}`}
                    onClick={() => removeEntry(entry.key)}
                    className="rounded-full px-1 text-[10px] font-semibold hover:bg-blue-200"
                    aria-label={`Remove label ${token}`}
                  >
                    ×
                  </button>
                )}
                {isEditing && (
                  <span
                    data-testid={`label-editor-editing-${entry.key}`}
                    className="text-[10px] font-semibold uppercase tracking-wide"
                  >
                    editing
                  </span>
                )}
              </div>
            )
          })
        )}
      </div>

      {!disabled && (
        <form onSubmit={handleSubmit} className="space-y-2" data-testid="label-editor-form">
          <div className="flex flex-col gap-1 sm:flex-row sm:items-start">
            <div className="flex-1">
              <label
                htmlFor={`${inputIdPrefix}-key`}
                className="block text-[11px] text-muted-foreground/70"
              >
                Key
              </label>
              <Input
                id={`${inputIdPrefix}-key`}
                type="text"
                value={draftKey}
                placeholder="stream"
                disabled={disabled}
                onChange={(e) => handleDraftKeyChange(e.target.value)}
                data-testid="label-editor-key-input"
                aria-invalid={draftError !== null}
                autoComplete="off"
              />
            </div>
            <div className="flex-1">
              <label
                htmlFor={`${inputIdPrefix}-value`}
                className="block text-[11px] text-muted-foreground/70"
              >
                Value
              </label>
              <Input
                id={`${inputIdPrefix}-value`}
                type="text"
                value={draftValue}
                placeholder="frontend"
                disabled={disabled}
                onChange={(e) => handleDraftValueChange(e.target.value)}
                data-testid="label-editor-value-input"
                aria-invalid={draftError !== null}
                autoComplete="off"
              />
            </div>
            <div className="flex items-end gap-1">
              <Button
                type="submit"
                size="xs"
                variant="outline"
                data-testid="label-editor-submit"
                disabled={disabled}
              >
                {submitLabel}
              </Button>
              {editingKey && (
                <Button
                  type="button"
                  size="xs"
                  variant="ghost"
                  data-testid="label-editor-cancel"
                  onClick={cancelEdit}
                >
                  Cancel
                </Button>
              )}
            </div>
          </div>
          {draftError && (
            <div
              data-testid="label-editor-error"
              className="text-xs text-red-600"
              role="alert"
            >
              {draftError}
            </div>
          )}
        </form>
      )}
    </div>
  )
}