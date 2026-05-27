import { useState } from 'react'
import { Dialog } from '../../../shared/ui/Dialog'
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
      <Dialog open={open} onClose={resetAndClose} title="Create Project">
        <div className="space-y-3">
          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">
              Name *
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Project name"
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 placeholder-gray-400 focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              autoFocus
            />
          </div>

          <div>
            <label className="block text-xs font-medium text-gray-700 mb-1">
              Path *
            </label>
            <div className="flex gap-2">
              <input
                type="text"
                value={path}
                readOnly
                placeholder="Select a directory..."
                className="flex-1 rounded-md border border-gray-300 px-3 py-2 text-sm text-gray-900 bg-gray-50 placeholder-gray-400 cursor-default"
              />
              <button
                onClick={() => setBrowseOpen(true)}
                className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors shrink-0"
              >
                Browse
              </button>
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
            <button
              onClick={resetAndClose}
              className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={handleCreate}
              disabled={
                !name.trim() ||
                !path ||
                createProject.isPending
              }
              className="rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50 transition-colors"
            >
              {createProject.isPending ? 'Creating...' : 'Create'}
            </button>
          </div>
        </div>
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
