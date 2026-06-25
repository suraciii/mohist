import { useRef, useState } from 'react'
import { PencilIcon, PlusIcon, Trash2Icon } from 'lucide-react'
import {
  isValidLabelKey,
  useCreateLabelDefinition,
  useDeleteLabelDefinition,
  useLabelCatalog,
  useUpdateLabelDefinition,
} from '../../../entities/label-catalog'
import type { LabelDefinition } from '../../../entities/label-catalog'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { Textarea } from '@/shared/ui/components/textarea'
import { SectionState } from './SectionState'
import { SettingsSection } from './SettingsSection'

interface DraftState {
  key: string
  description: string
  supportedValuesRaw: string
}

const emptyDraft: DraftState = { key: '', description: '', supportedValuesRaw: '' }

function draftFromDefinition(definition: LabelDefinition): DraftState {
  return {
    key: definition.key,
    description: definition.description,
    supportedValuesRaw: (definition.supportedValues ?? []).join(', '),
  }
}

function parseSupportedValues(raw: string): { values: string[]; error?: string } {
  const tokens = raw.split(/[,\n]/)
  const values = tokens.map((v) => v.trim())
  if (values.every((v) => v.length === 0)) {
    return { values: [], error: 'Provide at least one supported value (comma- or newline-separated).' }
  }
  const hasEmpty = values.some((v) => v.length === 0)
  if (hasEmpty) {
    return { values: values.filter((v) => v.length > 0), error: 'Supported values cannot contain empty entries.' }
  }
  return { values }
}

function parseOptionalSupportedValues(raw: string): { values?: string[]; error?: string } {
  if (raw.trim() === '') return { values: undefined }
  const parsed = parseSupportedValues(raw)
  if (parsed.error) return { error: parsed.error }
  return { values: parsed.values }
}

function validateDraft(
  draft: DraftState,
  mode: 'add' | 'edit',
): { values?: Omit<DraftState, 'supportedValuesRaw'> & { supportedValues?: string[] }; error?: string } {
  if (mode === 'add') {
    if (!draft.key.trim()) return { error: 'Key is required.' }
    if (!isValidLabelKey(draft.key.trim())) {
      return {
        error:
          "Key must match '^[a-z0-9]([-a-z0-9]*[a-z0-9])?$' (lowercase alphanumerics with optional interior dashes).",
      }
    }
  }
  if (!draft.description.trim()) {
    return { error: 'Description must be a non-empty, non-whitespace string.' }
  }
  const parsed = parseOptionalSupportedValues(draft.supportedValuesRaw)
  if (parsed.error) return { error: parsed.error }
  return {
    values: {
      key: draft.key.trim(),
      description: draft.description.trim(),
      supportedValues: parsed.values,
    },
  }
}

function LabelDefinitionRow({
  definition,
  isEditing,
  onStartEdit,
  onCancelEdit,
  onSave,
  draft,
  setDraft,
  editError,
  isSaving,
  onDelete,
  isDeleting,
}: {
  definition: LabelDefinition
  isEditing: boolean
  onStartEdit: () => void
  onCancelEdit: () => void
  onSave: () => void
  draft: DraftState
  setDraft: (next: DraftState) => void
  editError: string | null
  isSaving: boolean
  onDelete: () => void
  isDeleting: boolean
}) {
  return (
    <CardSection
      data-testid={`label-catalog-row-${definition.key}`}
      className="space-y-2 p-3"
    >
      {isEditing ? (
        <div className="space-y-2" data-testid={`label-catalog-edit-${definition.key}`}>
          <div>
            <Label className="text-xs" htmlFor={`label-edit-key-${definition.key}`}>Key</Label>
            <Input
              id={`label-edit-key-${definition.key}`}
              value={draft.key}
              readOnly
              disabled
              className="min-h-11 text-sm font-mono bg-muted"
              data-testid={`label-catalog-edit-key-${definition.key}`}
            />
            <p className="mt-1 text-[11px] text-muted-foreground">
              Keys are immutable. Create a new entry to rename.
            </p>
          </div>
          <div>
            <Label className="text-xs" htmlFor={`label-edit-description-${definition.key}`}>Description</Label>
            <Input
              id={`label-edit-description-${definition.key}`}
              value={draft.description}
              onChange={(e) => setDraft({ ...draft, description: e.target.value })}
              className="min-h-11 text-sm"
              data-testid={`label-catalog-edit-description-${definition.key}`}
            />
          </div>
          <div>
            <Label className="text-xs" htmlFor={`label-edit-values-${definition.key}`}>
              Supported values
            </Label>
            <Textarea
              id={`label-edit-values-${definition.key}`}
              value={draft.supportedValuesRaw}
              onChange={(e) => setDraft({ ...draft, supportedValuesRaw: e.target.value })}
              placeholder="One value per line, or comma-separated"
              className="min-h-16 text-sm font-mono"
              data-testid={`label-catalog-edit-values-${definition.key}`}
            />
            <p className="mt-1 text-[11px] text-muted-foreground">
              Leave empty to remove the value constraint.
            </p>
          </div>
          {editError && (
            <p
              data-testid={`label-catalog-edit-error-${definition.key}`}
              className="text-xs text-red-700"
            >
              {editError}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={onCancelEdit}
              disabled={isSaving}
              className="min-h-11 px-3 py-2 text-xs"
              data-testid={`label-catalog-edit-cancel-${definition.key}`}
            >
              Cancel
            </Button>
            <Button
              size="sm"
              onClick={onSave}
              disabled={isSaving}
              className="min-h-11 px-3 py-2 text-xs"
              data-testid={`label-catalog-edit-save-${definition.key}`}
            >
              {isSaving ? 'Saving...' : 'Save'}
            </Button>
          </div>
        </div>
      ) : (
        <div className="flex items-start justify-between gap-3">
          <div className="min-w-0 flex-1 space-y-1">
            <div className="flex flex-wrap items-center gap-2">
              <span
                className="font-mono text-xs text-muted-foreground"
                data-testid={`label-catalog-key-${definition.key}`}
              >
                {definition.key}
              </span>
            </div>
            <p
              className="text-sm text-foreground"
              data-testid={`label-catalog-description-${definition.key}`}
            >
              {definition.description}
            </p>
            {definition.supportedValues && definition.supportedValues.length > 0 && (
              <div className="flex flex-wrap items-center gap-1.5">
                <span className="text-[10px] font-medium text-muted-foreground uppercase">
                  Values
                </span>
                {definition.supportedValues.map((v) => (
                  <span
                    key={v}
                    data-testid={`label-catalog-value-${definition.key}-${v}`}
                    className="rounded border border-border bg-background px-1.5 py-0.5 text-[10px] text-muted-foreground"
                  >
                    {v}
                  </span>
                ))}
              </div>
            )}
          </div>
          <div className="flex shrink-0 items-center gap-1">
            <Button
              variant="ghost"
              size="sm"
              onClick={onStartEdit}
              className="min-h-11 px-3 py-2 text-xs"
              data-testid={`label-catalog-edit-button-${definition.key}`}
            >
              <PencilIcon />
              Edit
            </Button>
            <Button
              variant="ghost"
              size="sm"
              onClick={onDelete}
              disabled={isDeleting}
              className="min-h-11 px-3 py-2 text-xs text-red-700 hover:text-red-800 hover:bg-red-50"
              data-testid={`label-catalog-delete-button-${definition.key}`}
            >
              <Trash2Icon />
              Delete
            </Button>
          </div>
        </div>
      )}
    </CardSection>
  )
}

export function LabelCatalogSection() {
  const { data: definitions, isLoading, isError, error, refetch } = useLabelCatalog()
  const create = useCreateLabelDefinition()
  const update = useUpdateLabelDefinition()
  const remove = useDeleteLabelDefinition()

  const [addDraft, setAddDraft] = useState<DraftState>(emptyDraft)
  const [addError, setAddError] = useState<string | null>(null)
  const [editingKey, setEditingKey] = useState<string | null>(null)
  const [editDraft, setEditDraft] = useState<DraftState>(emptyDraft)
  const [editError, setEditError] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [pendingDeleteKey, setPendingDeleteKey] = useState<string | null>(null)
  const keyInputRef = useRef<HTMLInputElement>(null)

  const sortedDefinitions = (definitions ?? []).slice().sort((a, b) => a.key.localeCompare(b.key))

  function resetAddForm() {
    setAddDraft(emptyDraft)
    setAddError(null)
  }

  function handleAdd() {
    setPageError(null)
    const result = validateDraft(addDraft, 'add')
    if (result.error || !result.values) {
      setAddError(result.error ?? 'Invalid input.')
      return
    }
    setAddError(null)
    create.mutate(
      {
        key: result.values.key,
        description: result.values.description,
        supportedValues: result.values.supportedValues,
      },
      {
        onSuccess: () => {
          resetAddForm()
        },
        onError: (err: Error) => {
          setPageError(err.message)
        },
      },
    )
  }

  function startEdit(definition: LabelDefinition) {
    setEditingKey(definition.key)
    setEditDraft(draftFromDefinition(definition))
    setEditError(null)
  }

  function cancelEdit() {
    setEditingKey(null)
    setEditDraft(emptyDraft)
    setEditError(null)
  }

  function handleSaveEdit() {
    if (!editingKey) return
    setEditError(null)
    const result = validateDraft(editDraft, 'edit')
    if (result.error || !result.values) {
      setEditError(result.error ?? 'Invalid input.')
      return
    }
    const patch: { description: string; supportedValues?: string[] } = {
      description: result.values.description,
    }
    const rawValues = editDraft.supportedValuesRaw
    if (rawValues.trim() === '') {
      patch.supportedValues = []
    } else {
      const parsed = parseSupportedValues(rawValues)
      if (parsed.error || !parsed.values.length) {
        setEditError(parsed.error ?? 'Provide at least one supported value.')
        return
      }
      patch.supportedValues = parsed.values
    }
    update.mutate(
      { key: editingKey, patch },
      {
        onSuccess: () => {
          cancelEdit()
        },
        onError: (err: Error) => {
          setEditError(err.message)
        },
      },
    )
  }

  function handleDelete(key: string) {
    setPageError(null)
    setPendingDeleteKey(key)
    remove.mutate(key, {
      onSettled: () => setPendingDeleteKey(null),
      onError: (err: Error) => {
        setPageError(err.message)
      },
    })
  }

  return (
    <SettingsSection
      title="Label catalog"
      description="Define the labels your project suggests for issues. This catalog is advisory — issues can still carry any free-form label, and edits here do not change existing issue labels."
    >
      <div className="flex justify-end">
        <Button
          size="sm"
          variant="outline"
          onClick={() => keyInputRef.current?.focus()}
          className="min-h-11 px-3 py-2 text-xs"
          data-testid="label-catalog-focus-add"
        >
          <PlusIcon />
          New definition
        </Button>
      </div>

      {pageError && (
        <div
          data-testid="label-catalog-page-error"
          className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-xs text-red-700"
          role="alert"
        >
          {pageError}
        </div>
      )}

      <CardSection title="Add definition" titleAs="h3" className="p-3" data-testid="label-catalog-add-form">
        <div className="space-y-2">
          <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
            <div className="min-w-0">
              <Label className="text-xs" htmlFor="label-catalog-add-key">Key</Label>
              <Input
                ref={keyInputRef}
                id="label-catalog-add-key"
                value={addDraft.key}
                onChange={(e) => setAddDraft({ ...addDraft, key: e.target.value })}
                placeholder="module"
                className="min-h-11 text-sm font-mono"
                data-testid="label-catalog-add-key"
              />
            </div>
            <div className="min-w-0">
              <Label className="text-xs" htmlFor="label-catalog-add-description">Description</Label>
              <Input
                id="label-catalog-add-description"
                value={addDraft.description}
                onChange={(e) => setAddDraft({ ...addDraft, description: e.target.value })}
                placeholder="Classifies the subsystem"
                className="min-h-11 text-sm"
                data-testid="label-catalog-add-description"
              />
            </div>
          </div>
          <div>
            <Label className="text-xs" htmlFor="label-catalog-add-values">Supported values (optional)</Label>
            <Textarea
              id="label-catalog-add-values"
              value={addDraft.supportedValuesRaw}
              onChange={(e) => setAddDraft({ ...addDraft, supportedValuesRaw: e.target.value })}
              placeholder="auth, ui, persistence"
              className="min-h-16 text-sm font-mono"
              data-testid="label-catalog-add-values"
            />
          </div>
          {addError && (
            <p data-testid="label-catalog-add-error" className="text-xs text-red-700">
              {addError}
            </p>
          )}
          <Button
            onClick={handleAdd}
            disabled={create.isPending}
            size="sm"
            className="w-full min-h-11"
            data-testid="label-catalog-add-submit"
          >
            {create.isPending ? 'Adding...' : 'Add definition'}
          </Button>
        </div>
      </CardSection>

      <div data-testid="label-catalog-list">
        {isLoading ? (
          <SectionState variant="loading" title="Definitions" skeletonRows={3} />
        ) : isError ? (
          <SectionState
            variant="error"
            title="Definitions"
            message={error instanceof Error ? error.message : 'Failed to load catalog.'}
            onRetry={() => refetch()}
          />
        ) : sortedDefinitions.length === 0 ? (
          <SectionState
            variant="empty"
            title="Definitions"
            description="No label definitions yet. Add one above to start curating your project's catalog."
          />
        ) : (
          <div className="space-y-2">
            {sortedDefinitions.map((definition) => (
              <LabelDefinitionRow
                key={definition.key}
                definition={definition}
                isEditing={editingKey === definition.key}
                onStartEdit={() => startEdit(definition)}
                onCancelEdit={cancelEdit}
                onSave={handleSaveEdit}
                draft={editDraft}
                setDraft={setEditDraft}
                editError={editingKey === definition.key ? editError : null}
                isSaving={update.isPending && editingKey === definition.key}
                onDelete={() => handleDelete(definition.key)}
                isDeleting={pendingDeleteKey === definition.key && remove.isPending}
              />
            ))}
          </div>
        )}
      </div>
    </SettingsSection>
  )
}
