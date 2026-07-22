import { useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle2Icon, CircleAlertIcon, Loader2Icon, RotateCcwIcon, SaveIcon, XIcon } from 'lucide-react'
import { Button } from '@/shared/ui/components/button'
import { CardSection } from '@/shared/ui/components/card-section'
import { Input } from '@/shared/ui/components/input'
import {
  useExtractVariables,
  usePreviewProjectTemplate,
  useUpsertProjectTemplateOverride,
} from '../../../entities/template'
import type { ProjectTemplate } from '../../../entities/template'

export type EditorMode = 'override' | 'edit' | 'preview'

export interface TemplateEditorTarget {
  mode: EditorMode
  template: ProjectTemplate
  initialBody: string
  initialDisplayName: string
  initialDescription: string
  initialTags: string[]
  initialStage: string | null
}

export interface TemplateEditorProps {
  projectId: string
  target: TemplateEditorTarget
  onClose: () => void
  hooks?: TemplateEditorHooks
}

export interface TemplateEditorHooks {
  useUpsert: typeof useUpsertProjectTemplateOverride
  usePreview: typeof usePreviewProjectTemplate
  useExtract: typeof useExtractVariables
}

const defaultHooks: TemplateEditorHooks = {
  useUpsert: useUpsertProjectTemplateOverride,
  usePreview: usePreviewProjectTemplate,
  useExtract: useExtractVariables,
}

interface FormSnapshot {
  displayName: string
  description: string
  tagsText: string
  stage: string
  body: string
}

interface PreviewVariable {
  name: string
  available: boolean
}

const DEFAULT_PREVIEW_VARIABLES_JSON = `{
  "issue": {
    "number": 1,
    "projectId": "demo-project",
    "title": "Example issue title"
  },
  "repository": {
    "baseBranch": "main"
  },
  "workspace": {
    "branch": "feature/issue-1"
  },
  "vars": {
    "agent": {}
  }
}`

function buildSnapshot(target: TemplateEditorTarget): FormSnapshot {
  return {
    displayName: target.initialDisplayName,
    description: target.initialDescription,
    tagsText: target.initialTags.join(', '),
    stage: target.initialStage ?? '',
    body: target.initialBody,
  }
}

function parseTags(text: string): string[] {
  return text
    .split(',')
    .map((t) => t.trim())
    .filter((t) => t.length > 0)
}

function parseVariablesJson(text: string): { value: Record<string, unknown> | null; error: string | null } {
  const trimmed = text.trim()
  if (!trimmed) {
    return { value: {}, error: null }
  }
  try {
    const parsed = JSON.parse(trimmed)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return { value: null, error: 'Preview variables must be a JSON object' }
    }
    return { value: parsed as Record<string, unknown>, error: null }
  } catch (err) {
    return { value: null, error: (err as Error).message || 'Invalid JSON' }
  }
}

function lookupPath(value: unknown, path: string): boolean {
  if (value === undefined || value === null) return false
  const segments = path.split('.')
  let current: unknown = value
  for (const seg of segments) {
    if (current === null || current === undefined) return false
    if (typeof current !== 'object') return false
    if (Array.isArray(current)) {
      const index = Number(seg)
      if (!Number.isInteger(index) || index < 0 || index >= current.length) return false
      current = current[index]
      continue
    }
    const record = current as Record<string, unknown>
    if (!Object.prototype.hasOwnProperty.call(record, seg)) return false
    current = record[seg]
  }
  return current !== undefined
}

function buildPayload(snapshot: FormSnapshot, fallbackKey: string) {
  const tags = parseTags(snapshot.tagsText)
  return {
    displayName: snapshot.displayName.trim() || fallbackKey,
    description: snapshot.description.trim(),
    tags,
    stage: snapshot.stage.trim() ? snapshot.stage.trim() : null,
    body: snapshot.body,
  }
}

function titleByMode(mode: EditorMode, key: string): string {
  switch (mode) {
    case 'override':
      return `Override ${key}`
    case 'edit':
      return `Edit ${key}`
    case 'preview':
      return `Preview ${key}`
  }
}

export function TemplateEditor({
  projectId,
  target,
  onClose,
  hooks = defaultHooks,
}: TemplateEditorProps) {
  const initial = useMemo(() => buildSnapshot(target), [target])
  const [snapshot, setSnapshot] = useState<FormSnapshot>(initial)
  const [previewVariablesText, setPreviewVariablesText] = useState<string>(DEFAULT_PREVIEW_VARIABLES_JSON)
  const [debouncedBody, setDebouncedBody] = useState<string>(initial.body)
  const [debouncedVariablesText, setDebouncedVariablesText] = useState<string>(DEFAULT_PREVIEW_VARIABLES_JSON)
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  const isReadOnly = target.mode === 'preview'
  const upsert = hooks.useUpsert(projectId)
  const preview = hooks.usePreview(projectId, target.template.key)
  const extract = hooks.useExtract()
  const { mutate: previewTemplate } = preview
  const { mutate: extractVariables, data: extractData } = extract

  useEffect(() => {
    if (debounceRef.current) clearTimeout(debounceRef.current)
    debounceRef.current = setTimeout(() => {
      setDebouncedBody(snapshot.body)
      setDebouncedVariablesText(previewVariablesText)
    }, 300)
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current)
    }
  }, [snapshot.body, previewVariablesText])

  useEffect(() => {
    const parsed = parseVariablesJson(debouncedVariablesText)
    if (parsed.value === null) {
      return
    }
    previewTemplate({ variables: parsed.value })
    extractVariables({ body: debouncedBody })
  }, [debouncedBody, debouncedVariablesText, previewTemplate, extractVariables])

  function update<K extends keyof FormSnapshot>(key: K, value: FormSnapshot[K]) {
    setSnapshot((s) => ({ ...s, [key]: value }))
  }

  function handleReset() {
    setSnapshot(initial)
    setPreviewVariablesText(DEFAULT_PREVIEW_VARIABLES_JSON)
  }

  function handleSave() {
    const payload = buildPayload(snapshot, target.template.key)
    if (!payload.body.trim()) return
    upsert.mutate(
      { key: target.template.key, payload },
      {
        onSuccess: () => {
          onClose()
        },
      },
    )
  }

  const variables = useMemo<PreviewVariable[]>(() => {
    const list = extractData?.variables ?? []
    const parsed = parseVariablesJson(previewVariablesText)
    if (parsed.value === null) {
      return list.map((name) => ({ name, available: false }))
    }
    return list.map((name) => ({ name, available: lookupPath(parsed.value, name) }))
  }, [extractData, previewVariablesText])

  const parsedVariables = parseVariablesJson(previewVariablesText)
  const variablesValid = parsedVariables.error === null
  const bodyTrimmed = snapshot.body.trim()
  const canSave = !isReadOnly && bodyTrimmed.length > 0 && !upsert.isPending

  return (
    <CardSection
      data-testid="template-editor"
      className="space-y-3"
    >
      <div className="flex items-center justify-between">
        <h4 className="text-sm font-medium text-foreground">
          {titleByMode(target.mode, target.template.key)}
        </h4>
        <Button
          variant="ghost"
          size="icon"
          onClick={onClose}
          aria-label="Close editor"
          className="min-h-[48px] min-w-[48px]"
          data-testid="template-editor-close"
        >
          <XIcon />
        </Button>
      </div>

      <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
        <div className="space-y-3">
          <div>
            <label
              htmlFor="template-editor-key"
              className="block text-xs font-medium text-muted-foreground"
            >
              Key
            </label>
            <Input
              id="template-editor-key"
              value={target.template.key}
              readOnly
              disabled
              data-testid="template-editor-key"
              className="min-h-[48px] text-sm font-mono"
            />
          </div>
          <div>
            <label
              htmlFor="template-editor-displayname"
              className="block text-xs font-medium text-muted-foreground"
            >
              Display Name
            </label>
            <Input
              id="template-editor-displayname"
              value={snapshot.displayName}
              onChange={(e) => update('displayName', e.target.value)}
              disabled={isReadOnly}
              data-testid="template-editor-displayname"
              className="min-h-[48px] text-sm"
            />
          </div>
          <div>
            <label
              htmlFor="template-editor-description"
              className="block text-xs font-medium text-muted-foreground"
            >
              Description
            </label>
            <Input
              id="template-editor-description"
              value={snapshot.description}
              onChange={(e) => update('description', e.target.value)}
              disabled={isReadOnly}
              data-testid="template-editor-description"
              className="min-h-[48px] text-sm"
            />
          </div>
          <div>
            <label
              htmlFor="template-editor-tags"
              className="block text-xs font-medium text-muted-foreground"
            >
              Tags (comma separated)
            </label>
            <Input
              id="template-editor-tags"
              value={snapshot.tagsText}
              onChange={(e) => update('tagsText', e.target.value)}
              disabled={isReadOnly}
              data-testid="template-editor-tags"
              className="min-h-[48px] text-sm"
            />
          </div>
          <div>
            <label
              htmlFor="template-editor-stage"
              className="block text-xs font-medium text-muted-foreground"
            >
              Stage
            </label>
            <Input
              id="template-editor-stage"
              value={snapshot.stage}
              onChange={(e) => update('stage', e.target.value)}
              disabled={isReadOnly}
              data-testid="template-editor-stage"
              placeholder="plan, build, ..."
              className="min-h-[48px] text-sm"
            />
          </div>
          <div>
            <label
              htmlFor="template-editor-body"
              className="block text-xs font-medium text-muted-foreground"
            >
              Body
            </label>
            <textarea
              id="template-editor-body"
              value={snapshot.body}
              onChange={(e) => update('body', e.target.value)}
              readOnly={isReadOnly}
              data-testid="template-editor-body"
              className="min-h-[200px] w-full rounded-lg border border-input bg-transparent px-2.5 py-1.5 font-mono text-xs outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            />
          </div>
        </div>

        <div className="space-y-3">
          <div>
            <label
              htmlFor="template-editor-preview-vars"
              className="block text-xs font-medium text-muted-foreground"
            >
              Preview Variables (JSON object)
            </label>
            <textarea
              id="template-editor-preview-vars"
              value={previewVariablesText}
              onChange={(e) => setPreviewVariablesText(e.target.value)}
              data-testid="template-editor-preview-vars"
              spellCheck={false}
              className="min-h-[120px] w-full rounded-lg border border-input bg-transparent px-2.5 py-1.5 font-mono text-xs outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50"
            />
            {!variablesValid && parsedVariables.error && (
              <p
                data-testid="template-editor-preview-vars-error"
                className="mt-1 text-[11px] text-red-700"
              >
                {parsedVariables.error}
              </p>
            )}
          </div>

          <div>
            <label
              htmlFor="template-editor-preview"
              className="block text-xs font-medium text-muted-foreground"
            >
              Preview
            </label>
            <div
              id="template-editor-preview"
              data-testid="template-editor-preview"
              className="min-h-[200px] whitespace-pre-wrap rounded-lg border border-input bg-muted/30 px-2.5 py-2 font-mono text-xs"
            >
              {preview.isPending && !preview.data ? (
                <span
                  data-testid="template-editor-preview-loading"
                  className="inline-flex items-center gap-1 text-muted-foreground"
                >
                  <Loader2Icon className="size-3 animate-spin" /> Rendering...
                </span>
              ) : preview.isError ? (
                <span
                  data-testid="template-editor-preview-error"
                  className="text-red-700"
                >
                  Preview failed: {(preview.error as Error).message}
                </span>
              ) : (
                preview.data?.rendered ?? snapshot.body
              )}
            </div>
          </div>

          <div>
            <div className="text-xs font-medium text-muted-foreground">Referenced Variables</div>
            {variables.length === 0 ? (
              <p
                data-testid="template-editor-variables-empty"
                className="mt-1 text-[11px] text-muted-foreground"
              >
                {'No ${{ ... }} references in this body.'}
              </p>
            ) : (
              <ul
                data-testid="template-editor-variables"
                className="mt-1 space-y-1"
              >
                {variables.map((v) => (
                  <li
                    key={v.name}
                    data-testid={`template-editor-variable-${v.name}`}
                    data-available={v.available ? 'yes' : 'no'}
                    className="flex items-center gap-1 font-mono text-[11px]"
                  >
                    {v.available ? (
                      <CheckCircle2Icon
                        data-testid="template-editor-variable-available"
                        className="size-3 text-emerald-600"
                      />
                    ) : (
                      <CircleAlertIcon
                        data-testid="template-editor-variable-missing"
                        className="size-3 text-amber-600"
                      />
                    )}
                    <span>{v.name}</span>
                    <span className="text-muted-foreground">
                      {v.available ? 'available' : 'missing'}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </div>
      </div>

      <div className="flex justify-end gap-2">
        <Button
          variant="ghost"
          size="sm"
          onClick={onClose}
          className="min-h-[44px] px-3 py-2"
          data-testid="template-editor-cancel"
        >
          Cancel
        </Button>
        {!isReadOnly && (
          <Button
            variant="outline"
            size="sm"
            onClick={handleReset}
            className="min-h-[44px] px-3 py-2"
            data-testid="template-editor-reset"
          >
            <RotateCcwIcon />
            Reset
          </Button>
        )}
        {!isReadOnly && (
          <Button
            size="sm"
            disabled={!canSave}
            onClick={handleSave}
            className="min-h-[44px] px-3 py-2"
            data-testid="template-editor-save"
          >
            <SaveIcon />
            {upsert.isPending ? 'Saving...' : 'Save'}
          </Button>
        )}
      </div>
    </CardSection>
  )
}
