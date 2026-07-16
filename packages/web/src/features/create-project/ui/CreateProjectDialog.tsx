import { useState } from 'react'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/shared/ui/components/dialog'
import { Button } from '@/shared/ui/components/button'
import { Input } from '@/shared/ui/components/input'
import { Label } from '@/shared/ui/components/label'
import { useCreateProject, useProject } from '../../../entities/project'
import type { ProjectCreator } from '../../../entities/project'

interface Props {
  open: boolean
  onClose: () => void
  projectCreator?: ProjectCreator
}

export function CreateProjectDialog({ open, onClose, projectCreator }: Props) {
  const [name, setName] = useState('')
  const [repositoryName, setRepositoryName] = useState('')
  const [repositoryGitUrl, setRepositoryGitUrl] = useState('')
  const [repositoryBaseBranch, setRepositoryBaseBranch] = useState('main')
  const { setProjectId } = useProject()

  const createProject = useCreateProject(projectCreator)

  const isConflict =
    createProject.isError &&
    (createProject.error as Error).message.includes('already exists')

  function resetAndClose() {
    setName('')
    setRepositoryName('')
    setRepositoryGitUrl('')
    setRepositoryBaseBranch('main')
    createProject.reset()
    onClose()
  }

  async function handleCreate() {
    createProject.mutate(
      {
        name: name.trim(),
        repository: {
          name: repositoryName.trim(),
          gitUrl: repositoryGitUrl.trim(),
          baseBranch: repositoryBaseBranch.trim() || undefined,
        },
      },
      {
        onSuccess: (project) => {
          setProjectId(project.id)
          resetAndClose()
        },
      },
    )
  }

  return (
    <Dialog open={open} onOpenChange={(v) => !v && resetAndClose()}>
      <DialogContent data-testid="create-project-dialog">
        <DialogHeader>
          <DialogTitle>Create Project</DialogTitle>
        </DialogHeader>
        <div className="space-y-3">
          <div>
            <Label htmlFor="project-name" className="text-xs">Name *</Label>
            <Input
              id="project-name"
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Project name"
              autoFocus
              data-testid="create-project-name"
            />
          </div>
          <div>
            <Label htmlFor="project-repository-name" className="text-xs">Repository name *</Label>
            <Input
              id="project-repository-name"
              type="text"
              value={repositoryName}
              onChange={(e) => setRepositoryName(e.target.value)}
              placeholder="server"
              data-testid="create-project-repository-name"
            />
          </div>
          <div>
            <Label htmlFor="project-repository-git-url" className="text-xs">Git URL *</Label>
            <Input
              id="project-repository-git-url"
              type="url"
              value={repositoryGitUrl}
              onChange={(e) => setRepositoryGitUrl(e.target.value)}
              placeholder="https://github.com/your-org/server.git"
              data-testid="create-project-repository-git-url"
            />
          </div>
          <div>
            <Label htmlFor="project-repository-base-branch" className="text-xs">Base branch</Label>
            <Input
              id="project-repository-base-branch"
              type="text"
              value={repositoryBaseBranch}
              onChange={(e) => setRepositoryBaseBranch(e.target.value)}
              placeholder="main"
              data-testid="create-project-repository-base-branch"
            />
          </div>

          {isConflict && (
            <div
              data-testid="create-project-conflict"
              className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600"
            >
              Project name already exists
            </div>
          )}

          {!isConflict && createProject.isError && (
            <div
              data-testid="create-project-error"
              className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600"
            >
              {createProject.error.message}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <Button
              variant="outline"
              onClick={resetAndClose}
            >
              Cancel
            </Button>
            <Button
              onClick={handleCreate}
              disabled={
                !name.trim() ||
                !repositoryName.trim() ||
                !repositoryGitUrl.trim() ||
                createProject.isPending
              }
              data-testid="create-project-submit"
            >
              {createProject.isPending ? 'Creating...' : 'Create'}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
