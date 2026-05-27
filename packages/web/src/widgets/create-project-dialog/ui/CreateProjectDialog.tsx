import { useState } from 'react'
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { DialogSelectDirectory } from '../../../shared/ui/DialogSelectDirectory'
import { useCreateProject, useUseProject } from '../../../entities/project/api/queries'
import { useProject } from '../../../entities/project/model/ProjectContext'

interface Props {
  open: boolean
  onClose: () => void
}

export function CreateProjectDialog({ open, onClose }: Props) {
  const [name, setName] = useState('')
  const [path, setPath] = useState('')
  const [browseOpen, setBrowseOpen] = useState(false)
  const { setProjectId, projects } = useProject()

  const createProject = useCreateProject()
  const switchProject = useUseProject()
  const [switchError, setSwitchError] = useState('')

  const isConflict =
    createProject.isError &&
    (createProject.error as Error).message.includes('already exists')

  function resetAndClose() {
    setName('')
    setPath('')
    setSwitchError('')
    createProject.reset()
    onClose()
  }

  async function handleCreate() {
    setSwitchError('')
    createProject.mutate(
      { name: name.trim(), path },
      {
        onSuccess: (project) => {
          switchProject.mutate(project.name, {
            onSuccess: () => {
              setProjectId(project.id)
              resetAndClose()
            },
            onError: (err) => {
              setSwitchError(err instanceof Error ? err.message : 'Failed to switch project')
            },
          })
        },
      },
    )
  }

  return (
    <>
      <Dialog open={open} onOpenChange={(v) => !v && resetAndClose()}>
        <DialogContent>
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
              />
            </div>

            <div>
              <Label htmlFor="project-path" className="text-xs">Path *</Label>
              <div className="flex gap-2">
                <Input
                  id="project-path"
                  type="text"
                  value={path}
                  readOnly
                  placeholder="Select a directory..."
                  className="bg-muted cursor-default"
                />
                <Button
                  variant="outline"
                  onClick={() => setBrowseOpen(true)}
                >
                  Browse
                </Button>
              </div>
            </div>

            {isConflict && (
              <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                Project name already exists
              </div>
            )}

            {!isConflict && createProject.isError && (
              <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
                {createProject.error.message}
              </div>
            )}

            {switchError && (
              <div className="rounded-md bg-yellow-50 px-3 py-2 text-xs text-yellow-700">
                Project created, but failed to switch: {switchError}
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
                  !path ||
                  createProject.isPending
                }
              >
                {createProject.isPending ? 'Creating...' : 'Create'}
              </Button>
            </div>
          </div>
        </DialogContent>
      </Dialog>

      <DialogSelectDirectory
        open={browseOpen}
        recentProjects={projects}
        onClose={() => setBrowseOpen(false)}
        onSelect={(selectedPath) => {
          setPath(selectedPath)
          setBrowseOpen(false)
        }}
      />
    </>
  )
}
