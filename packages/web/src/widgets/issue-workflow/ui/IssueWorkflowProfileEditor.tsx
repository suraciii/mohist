import { useState, useEffect, useCallback } from 'react'
import { Button } from '@/shared/ui/components/button'
import { Textarea } from '@/shared/ui/components/textarea'
import { useIssueWorkflowProfileYaml, useUpdateIssueWorkflowProfileYaml } from '../../../entities/issue'
import type { IssueWorkflowProfileYamlResponse } from '../../../entities/issue'

interface IssueWorkflowProfileEditorProps {
  issueNumber: number
}

interface ValidationError {
  code: string
  message: string
}

export function IssueWorkflowProfileEditor({ issueNumber }: IssueWorkflowProfileEditorProps) {
  const [draftYaml, setDraftYaml] = useState('')
  const [serverYaml, setServerYaml] = useState<string | null>(null)
  const [validationErrors, setValidationErrors] = useState<ValidationError[]>([])
  const [saveSuccess, setSaveSuccess] = useState(false)

  const { data, isLoading, error: fetchError } = useIssueWorkflowProfileYaml(issueNumber, true)
  const updateMutation = useUpdateIssueWorkflowProfileYaml()

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

  const isDirty = serverYaml !== null && draftYaml !== serverYaml

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

  if (isLoading) {
    return (
      <div className="rounded-lg border border-gray-200 bg-white p-4">
        <div className="flex items-center justify-between mb-3">
          <h3 className="text-sm font-semibold text-gray-700">Workflow Profile</h3>
        </div>
        <div className="space-y-2">
          {[1, 2, 3, 4].map((i) => (
            <div key={i} className="h-4 bg-gray-100 rounded animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  if (fetchError) {
    return (
      <div className="rounded-lg border border-red-200 bg-red-50 p-4">
        <p className="text-xs text-red-600">Failed to load workflow profile: {(fetchError as Error).message}</p>
      </div>
    )
  }

  const hasErrors = validationErrors.length > 0

  return (
    <div className="rounded-lg border border-gray-200 bg-white p-4">
      <div className="flex items-center justify-between mb-3">
        <div className="flex items-center gap-2">
          <h3 className="text-sm font-semibold text-gray-700">Workflow Profile</h3>
          {data?.workflowRunId && (
            <span className="text-xs text-muted-foreground bg-gray-100 px-1.5 py-0.5 rounded">
              Active run: {data.workflowRunId.slice(0, 8)}
            </span>
          )}
        </div>
        <div className="flex items-center gap-2">
          {isDirty && (
            <span className="text-xs text-amber-600 font-medium">Unsaved changes</span>
          )}
          {saveSuccess && (
            <span className="text-xs text-green-600 font-medium">Saved</span>
          )}
        </div>
      </div>

      <div className="space-y-2">
        <Textarea
          value={draftYaml}
          onChange={(e) => handleDraftChange(e.target.value)}
          rows={12}
          className="font-mono text-xs resize-none"
          placeholder="Loading workflow profile..."
          disabled={updateMutation.isPending}
        />

        {hasErrors && (
          <div className="rounded-md bg-red-50 border border-red-200 p-2 space-y-1">
            {validationErrors.map((err, idx) => (
              <p key={idx} className="text-xs text-red-600">
                <span className="font-medium uppercase">{err.code}:</span> {err.message}
              </p>
            ))}
          </div>
        )}

        <div className="flex items-center justify-between">
          <div className="text-xs text-muted-foreground">
            {data?.profileId && (
              <span>Profile: {data.profileId}</span>
            )}
          </div>
          <Button
            onClick={handleSave}
            disabled={!isDirty || updateMutation.isPending || !draftYaml.trim()}
            size="sm"
          >
            {updateMutation.isPending ? 'Saving...' : 'Save'}
          </Button>
        </div>
      </div>
    </div>
  )
}
