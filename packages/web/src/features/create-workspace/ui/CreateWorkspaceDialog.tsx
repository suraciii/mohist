import { useEffect, useMemo, useState, type FormEvent } from 'react'
import { PlusIcon } from 'lucide-react'

import { useProject, useRepositories } from '@/entities/project'
import { useCreateWorkspace, type Workspace } from '@/entities/workspace'
import { Button } from '@/shared/ui/components/button'
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '@/shared/ui/components/dialog'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'

interface CreateWorkspaceDialogProps {
  open: boolean
  onClose: () => void
  initialRepositoryNames?: readonly string[]
  onCreated?: (workspace: Workspace) => void
}

function sameRepositoryName(left: string, right: string) {
  return left.localeCompare(right, undefined, { sensitivity: 'accent' }) === 0
}

function errorDetails(error: unknown) {
  if (!error || typeof error !== 'object') return { code: '', message: '', status: undefined }
  const details = error as { code?: unknown; message?: unknown; status?: unknown }
  return {
    code: typeof details.code === 'string' ? details.code.toLowerCase() : '',
    message: typeof details.message === 'string' ? details.message : '',
    status: typeof details.status === 'number' ? details.status : undefined,
  }
}

function creationErrorMessage(error: unknown) {
  const { code, message, status } = errorDetails(error)
  if (status === 409 || code.includes('taken') || code.includes('exists') || /already (exists|taken)/i.test(message)) {
    return 'A workspace with this name already exists.'
  }
  if (code.includes('repository') || /repository/i.test(message)) {
    return 'One or more selected repositories are unavailable.'
  }
  if (status !== undefined && status >= 500 || error instanceof TypeError) {
    return 'Workspace could not be created. Check your connection and try again.'
  }
  return 'Workspace could not be created. Check the name and repositories, then try again.'
}

export function CreateWorkspaceDialog({
  open,
  onClose,
  initialRepositoryNames = [],
  onCreated,
}: CreateWorkspaceDialogProps) {
  const { projectId } = useProject()
  const {
    data: repositories = [],
    isLoading: repositoriesLoading,
    isError: repositoriesFailed,
    refetch: refetchRepositories,
  } = useRepositories(projectId ?? undefined)
  const createWorkspace = useCreateWorkspace()
  const [name, setName] = useState('')
  const [nameTouched, setNameTouched] = useState(false)
  const [selectedRepositoryNames, setSelectedRepositoryNames] = useState<string[]>([])

  useEffect(() => {
    if (!open) return
    setName('')
    setNameTouched(false)
    setSelectedRepositoryNames([...initialRepositoryNames])
    createWorkspace.reset()
  }, [open])

  const selectedRepositories = useMemo(
    () => repositories
      .filter((repository) => selectedRepositoryNames.some((name) => sameRepositoryName(name, repository.name)))
      .map((repository) => repository.name),
    [repositories, selectedRepositoryNames],
  )
  const trimmedName = name.trim()
  const nameInvalid = nameTouched && trimmedName.length === 0
  const createDisabled = !projectId || !trimmedName || repositoriesLoading || repositoriesFailed || createWorkspace.isPending

  function handleClose() {
    if (!createWorkspace.isPending) onClose()
  }

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setNameTouched(true)
    if (createDisabled) return

    createWorkspace.mutate(
      { name: trimmedName, repos: selectedRepositories },
      {
        onSuccess: (workspace) => {
          onCreated?.(workspace)
          onClose()
        },
      },
    )
  }

  return (
    <Dialog open={open} onOpenChange={(nextOpen) => !nextOpen && handleClose()}>
      <DialogContent className="sm:max-w-lg" data-testid="create-workspace-dialog">
        <DialogHeader>
          <DialogTitle>Create Workspace</DialogTitle>
        </DialogHeader>

        <form className="space-y-5" onSubmit={handleSubmit}>
          <div className="space-y-2">
            <Label htmlFor="create-workspace-name">Name</Label>
            <Input
              id="create-workspace-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
              onBlur={() => setNameTouched(true)}
              aria-invalid={nameInvalid}
              aria-required="true"
              data-testid="create-workspace-name"
              disabled={createWorkspace.isPending}
              autoFocus
            />
            {nameInvalid && (
              <p className="text-sm text-destructive" data-testid="create-workspace-name-error">
                Enter a workspace name.
              </p>
            )}
          </div>

          <fieldset className="space-y-2" disabled={createWorkspace.isPending || repositoriesLoading || repositoriesFailed}>
            <legend className="text-sm font-medium">Repositories</legend>
            {repositoriesLoading && <p data-testid="create-workspace-repositories-loading">Loading repositories...</p>}
            {repositoriesFailed && (
              <div className="space-y-2" data-testid="create-workspace-repositories-error">
                <p className="text-sm text-destructive">Project repositories could not be loaded.</p>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => refetchRepositories()}
                  data-testid="create-workspace-repositories-retry"
                >
                  Retry
                </Button>
              </div>
            )}
            {!repositoriesLoading && !repositoriesFailed && repositories.length === 0 && (
              <p className="text-sm text-muted-foreground" data-testid="create-workspace-repositories-empty">
                No project repositories are available.
              </p>
            )}
            {!repositoriesLoading && !repositoriesFailed && repositories.length > 0 && (
              <div className="space-y-2" data-testid="create-workspace-repositories">
                {repositories.map((repository) => {
                  const checked = selectedRepositoryNames.some((selected) => sameRepositoryName(selected, repository.name))
                  return (
                    <label className="flex items-center gap-2 text-sm" key={repository.name}>
                      <input
                        type="checkbox"
                        checked={checked}
                        onChange={() => setSelectedRepositoryNames((current) => checked
                          ? current.filter((selected) => !sameRepositoryName(selected, repository.name))
                          : [...current, repository.name])}
                        data-testid={`create-workspace-repository-${repository.name}`}
                      />
                      <span>{repository.name}</span>
                    </label>
                  )
                })}
              </div>
            )}
          </fieldset>

          {createWorkspace.isError && (
            <p className="text-sm text-destructive" data-testid="create-workspace-error">
              {creationErrorMessage(createWorkspace.error)}
            </p>
          )}

          <DialogFooter>
            <Button type="button" variant="ghost" onClick={handleClose} disabled={createWorkspace.isPending}>
              Cancel
            </Button>
            <Button type="submit" disabled={createDisabled} data-testid="create-workspace-submit">
              <PlusIcon className="mr-2 h-4 w-4" aria-hidden="true" />
              {createWorkspace.isPending ? 'Creating...' : 'Create Workspace'}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
