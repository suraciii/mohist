import { useState, useEffect, useCallback } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import {
  useDeleteIssueWorkflowProfileTemplate,
  useIssueWorkflowProfileYaml,
  useUpdateIssueWorkflowProfileYaml,
} from '../../../entities/issue'
import type { IssueWorkflowProfileYamlResponse } from '../../../entities/issue'

interface IssueWorkflowProfileEditorProps {
  issueNumber: number
  embedded?: boolean
}

interface ValidationError {
  code: string
  message: string
}

type EditorMode = 'view' | 'editing'

const templateSourceLabel: Record<'system' | 'project' | 'custom', string> = {
  system: 'System default',
  project: 'Project default',
  custom: 'Custom',
}

export function IssueWorkflowProfileEditor({ issueNumber, embedded = false }: IssueWorkflowProfileEditorProps) {
  const [draftYaml, setDraftYaml] = useState('')
  const [serverYaml, setServerYaml] = useState<string | null>(null)
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>([])
  const [saveSuccess, setSaveSuccess] = useState(false)
  const [revertError, setRevertError] = useState<string | null>(null)
  const [mode, setMode] = useState<EditorMode>('view')

  const { data, isLoading, error: fetchError, refetch } = useIssueWorkflowProfileYaml(issueNumber, true)
  const updateMutation = useUpdateIssueWorkflowProfileYaml()
  const deleteMutation = useDeleteIssueWorkflowProfileTemplate()

  useEffect(() => {
    if (data?.yaml !== undefined && data.yaml !== null) {
      setServerYaml((currentServerYaml) => {
        const nextServerYaml = data.yaml ?? ''

        setDraftYaml((currentDraftYaml) => {
          if (currentServerYaml === null || currentDraftYaml === currentServerYaml) {
            return nextServerYaml
          }

          return currentDraftYaml
        })

        setValidationErrors([])
        setSaveSuccess(false)
        return nextServerYaml
      })
    }
  }, [data])

  useEffect(() => {
    if (data && !data.hasCustomTemplate) {
      setMode('view')
      setRevertError(null)
    }
  }, [data])

  const isDirty =
    draftYaml.trim() !== '' && (serverYaml === null || draftYaml !== serverYaml)

  const handleDraftChange = useCallback((value: string) => {
    setDraftYaml(value)
    setValidationErrors([])
    setSaveSuccess(false)
  }, [])

  const handleSave = useCallback(() => {
    if (!draftYaml.trim()) return
    setValidationErrors([])
    setSaveSuccess(false)
    updateMutation.mutate(
      { issueNumber, yaml: draftYaml },
      {
        onSuccess: (response: IssueWorkflowProfileYamlResponse) => {
          setServerYaml(response.yaml)
          setDraftYaml(response.yaml ?? '')
          setValidationErrors([])
          setSaveSuccess(true)
          setTimeout(() => setSaveSuccess(false), 3000)
        },
        onError: (err: Error) => {
          const errorMessage = err.message || 'Save failed'
          if (errorMessage.includes('yaml_syntax') || errorMessage.toLowerCase().includes('yaml')) {
            setValidationErrors([{ code: 'yaml_syntax', message: errorMessage }])
          } else {
            setValidationErrors([{ code: 'workflow_shape', message: errorMessage }])
          }
        },
      }
    )
  }, [draftYaml, issueNumber, updateMutation])

  const handleRevert = useCallback(() => {
    setRevertError(null)
    deleteMutation.mutate(
      { issueNumber },
      {
        onError: (err: Error) => {
          setRevertError(err.message || 'Revert failed')
        },
      }
    )
  }, [deleteMutation, issueNumber])

  if (isLoading) {
    return <LoadingCard embedded={embedded} />
  }

  if (fetchError) {
    return <ErrorCard embedded={embedded} message={(fetchError as Error).message} onRetry={() => refetch()} />
  }

  if (!data) {
    return null
  }

  const isReference = !data.hasCustomTemplate && data.yaml == null
  const showEditor = !isReference || mode === 'editing'

  if (isReference && !showEditor) {
    return (
      <ReferenceSummaryCard
        data={data}
        embedded={embedded}
        onCustomize={() => {
          setMode('editing')
          setRevertError(null)
        }}
      />
    )
  }

  return (
    <CustomEditorCard
      data={data}
      embedded={embedded}
      draftYaml={draftYaml}
      serverYaml={serverYaml}
      isDirty={isDirty}
      saveSuccess={saveSuccess}
      validationErrors={validationErrors}
      revertError={revertError}
      isSaving={updateMutation.isPending}
      isReverting={deleteMutation.isPending}
      onDraftChange={handleDraftChange}
      onSave={handleSave}
      onRevert={handleRevert}
    />
  )
}

function LoadingCard({ embedded = false }: { embedded?: boolean }) {
  return (
    <div
      data-testid="workflow-profile-loading"
      className={embedded ? 'space-y-2' : 'rounded-lg border border-border bg-card p-4 space-y-2'}
    >
      <div className="h-3 w-32 bg-muted rounded animate-pulse" />
      <div className="h-3 w-48 bg-muted rounded animate-pulse" />
      <div className="h-3 w-40 bg-muted rounded animate-pulse" />
    </div>
  )
}

function ErrorCard({ embedded = false, message, onRetry }: { embedded?: boolean; message: string; onRetry: () => void }) {
  return (
    <div
      data-testid="workflow-profile-error"
      className={embedded ? 'space-y-2 text-danger' : 'rounded-lg border border-danger-border bg-danger-subtle p-4 space-y-2'}
    >
      <p className="text-xs text-danger">Failed to load workflow profile: {message}</p>
      <div>
        <Button variant="outline" size="sm" onClick={onRetry}>
          Retry
        </Button>
      </div>
    </div>
  )
}

function ReferenceSummaryCard({
  data,
  embedded = false,
  onCustomize,
}: {
  data: IssueWorkflowProfileYamlResponse
  embedded?: boolean
  onCustomize: () => void
}) {
  const source = data.templateSource ?? 'system'
  return (
    <div
      data-testid="workflow-profile-reference"
      className={embedded ? 'space-y-3' : 'rounded-lg border border-border bg-card p-4 space-y-3'}
    >
      {!embedded && <h3 className="text-sm font-semibold text-card-foreground">Workflow Profile</h3>}
      <dl className="text-xs space-y-1.5">
        <div className="flex justify-between gap-3">
          <dt className="text-muted-foreground">Profile</dt>
          <dd className="font-mono text-foreground text-right">{data.profileId}</dd>
        </div>
        <div className="flex justify-between gap-3">
          <dt className="text-muted-foreground">Mode</dt>
          <dd className="text-foreground text-right">Inherited</dd>
        </div>
        <div className="flex justify-between gap-3">
          <dt className="text-muted-foreground">Template</dt>
          <dd className="text-foreground text-right">{templateSourceLabel[source]}</dd>
        </div>
        <div className="flex justify-between gap-3">
          <dt className="text-muted-foreground">Overrides</dt>
          <dd className="text-foreground text-right">None</dd>
        </div>
      </dl>
      <p className="text-xs text-muted-foreground">
        This issue inherits its workflow profile. Customize it to add issue-owned workflow YAML.
      </p>
      <div>
        <Button size="sm" onClick={onCustomize}>
          Customize profile
        </Button>
      </div>
    </div>
  )
}

function CustomEditorCard({
  data,
  embedded = false,
  draftYaml,
  isDirty,
  saveSuccess,
  validationErrors,
  revertError,
  isSaving,
  isReverting,
  onDraftChange,
  onSave,
  onRevert,
}: {
  data: IssueWorkflowProfileYamlResponse
  embedded?: boolean
  draftYaml: string
  serverYaml: string | null
  isDirty: boolean
  saveSuccess: boolean
  validationErrors: ValidationError[]
  revertError: string | null
  isSaving: boolean
  isReverting: boolean
  onDraftChange: (value: string) => void
  onSave: () => void
  onRevert: () => void
}) {
  return (
    <div
      data-testid="workflow-profile-custom"
      className={embedded ? '' : 'rounded-lg border border-border bg-card p-4'}
    >
      <div className="flex items-center justify-between mb-2">
        {!embedded && <h3 className="text-sm font-semibold text-card-foreground">Workflow Profile</h3>}
        {data.workflowRunId && (
          <span className="text-xs text-muted-foreground bg-muted px-1.5 py-0.5 rounded">
            Active run: {data.workflowRunId.slice(0, 8)}
          </span>
        )}
      </div>
      <p className="text-xs text-muted-foreground mb-3">
        Editing this issue&apos;s own workflow profile YAML (not the active run YAML).
      </p>

      <div className="space-y-2">
        <Textarea
          value={draftYaml}
          onChange={(e) => onDraftChange(e.target.value)}
          rows={12}
          className="font-mono text-xs resize-none"
          placeholder=""
          disabled={isSaving || isReverting}
        />

        {validationErrors.length > 0 && (
          <div className="rounded-md bg-danger-subtle border border-danger-border p-2 space-y-1">
            {validationErrors.map((err, idx) => (
              <p key={idx} className="text-xs text-danger">
                <span className="font-medium uppercase">{err.code}:</span> {err.message}
              </p>
            ))}
          </div>
        )}

        {revertError && (
          <div className="rounded-md bg-danger-subtle border border-danger-border p-2">
            <p className="text-xs text-danger">
              <span className="font-medium uppercase">revert:</span> {revertError}
            </p>
          </div>
        )}

        <div className="flex items-center justify-between">
          <div className="text-xs text-muted-foreground flex items-center gap-3">
            {data.profileId && <span>Profile: {data.profileId}</span>}
            {isDirty && (
              <span className="text-warning font-medium">Unsaved changes</span>
            )}
            {saveSuccess && (
              <span className="text-success font-medium">Saved</span>
            )}
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={onRevert}
              disabled={isSaving || isReverting}
            >
              {isReverting ? 'Reverting...' : 'Revert to inherited profile'}
            </Button>
            <Button
              onClick={onSave}
              disabled={!isDirty || isSaving || isReverting || !draftYaml.trim()}
              size="sm"
            >
              {isSaving ? 'Saving...' : 'Save'}
            </Button>
          </div>
        </div>
      </div>
    </div>
  )
}
