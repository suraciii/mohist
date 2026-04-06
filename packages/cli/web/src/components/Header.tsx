import { useState, useRef, useEffect } from 'react'
import { useProject } from '../context/ProjectContext'
import type { Project } from '../lib/types'
import { CreateIssueDialog } from './CreateIssueDialog'
import { CreateProjectDialog } from './CreateProjectDialog'
import { Dialog } from './Dialog'
import { useDeleteProject, useUseProject } from '../hooks/useQueries'

export function Header() {
  const { projectId, setProjectId, projects, currentProject } = useProject()
  const [dropdownOpen, setDropdownOpen] = useState(false)
  const [createIssueOpen, setCreateIssueOpen] = useState(false)
  const [createProjectOpen, setCreateProjectOpen] = useState(false)
  const [deleteConfirmOpen, setDeleteConfirmOpen] = useState(false)
  const dropdownRef = useRef<HTMLDivElement>(null)

  const deleteProject = useDeleteProject()
  const switchProject = useUseProject()

  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (dropdownRef.current && !dropdownRef.current.contains(e.target as Node)) {
        setDropdownOpen(false)
      }
    }
    if (dropdownOpen) {
      document.addEventListener('mousedown', handleClickOutside)
      return () => document.removeEventListener('mousedown', handleClickOutside)
    }
  }, [dropdownOpen])

  const handleSelect = (project: Project) => {
    setProjectId(project.id)
    setDropdownOpen(false)
  }

  function handleDelete() {
    if (!currentProject) return
    deleteProject.mutate(currentProject.name, {
      onSuccess: () => {
        setDeleteConfirmOpen(false)
        setDropdownOpen(false)
        const remaining = projects.filter((p) => p.id !== currentProject.id)
        if (remaining.length > 0) {
          setProjectId(remaining[0].id)
          switchProject.mutate(remaining[0].name)
        } else {
          setProjectId(null)
        }
      },
    })
  }

  return (
    <>
      <header className="h-14 border-b border-gray-200 bg-white flex items-center px-6 shrink-0">
        <h1 className="text-lg font-bold text-gray-900 tracking-tight">mohist</h1>

        <div className="ml-6" ref={dropdownRef}>
          {projects.length === 0 ? (
            <span className="text-sm text-gray-600 font-medium">
              No projects
            </span>
          ) : projects.length === 1 ? (
            <span className="text-sm text-gray-600 font-medium">
              {currentProject?.name ?? 'Loading...'}
            </span>
          ) : (
            <div className="relative">
              <button
                onClick={() => setDropdownOpen(!dropdownOpen)}
                className="flex items-center gap-1.5 text-sm text-gray-700 hover:text-gray-900 font-medium px-2.5 py-1.5 rounded-md hover:bg-gray-100 transition-colors"
              >
                <span>{currentProject?.name ?? 'Select project'}</span>
                <svg
                  className={`h-4 w-4 text-gray-400 transition-transform ${dropdownOpen ? 'rotate-180' : ''}`}
                  viewBox="0 0 20 20"
                  fill="currentColor"
                >
                  <path
                    fillRule="evenodd"
                    d="M5.23 7.21a.75.75 0 011.06.02L10 11.168l3.71-3.938a.75.75 0 111.08 1.04l-4.25 4.5a.75.75 0 01-1.08 0l-4.25-4.5a.75.75 0 01.02-1.06z"
                    clipRule="evenodd"
                  />
                </svg>
              </button>

              {dropdownOpen && (
                <div className="absolute top-full left-0 mt-1 w-56 rounded-md border border-gray-200 bg-white shadow-lg py-1 z-50">
                  {projects.map((project) => (
                    <button
                      key={project.id}
                      onClick={() => handleSelect(project)}
                      className={`w-full text-left px-3 py-2 text-sm hover:bg-gray-50 transition-colors ${
                        project.id === projectId
                          ? 'text-blue-600 bg-blue-50 font-medium'
                          : 'text-gray-700'
                      }`}
                    >
                      <div className="font-medium">{project.name}</div>
                      <div className="text-xs text-gray-400 truncate mt-0.5">{project.path}</div>
                    </button>
                  ))}

                  <div className="border-t border-gray-100 my-1" />

                  <button
                    onClick={() => {
                      setDropdownOpen(false)
                      setCreateProjectOpen(true)
                    }}
                    className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                  >
                    New Project
                  </button>

                  <button
                    onClick={() => {
                      setDropdownOpen(false)
                      setDeleteConfirmOpen(true)
                    }}
                    className="w-full text-left px-3 py-2 text-sm text-red-600 hover:bg-red-50 transition-colors"
                  >
                    Delete Project
                  </button>
                </div>
              )}
            </div>
          )}
        </div>

        <div className="ml-auto">
          <button
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 transition-colors shadow-sm"
            onClick={() => setCreateIssueOpen(true)}
          >
            <svg className="h-4 w-4" viewBox="0 0 20 20" fill="currentColor">
              <path d="M10.75 4.75a.75.75 0 00-1.5 0v4.5h-4.5a.75.75 0 000 1.5h4.5v4.5a.75.75 0 001.5 0v-4.5h4.5a.75.75 0 000-1.5h-4.5v-4.5z" />
            </svg>
            New Issue
          </button>
        </div>
      </header>

      <CreateIssueDialog open={createIssueOpen} onClose={() => setCreateIssueOpen(false)} />
      <CreateProjectDialog open={createProjectOpen} onClose={() => setCreateProjectOpen(false)} />

      <Dialog
        open={deleteConfirmOpen}
        onClose={() => setDeleteConfirmOpen(false)}
        title="Delete Project"
      >
        <div className="space-y-3">
          <p className="text-sm text-gray-600">
            Are you sure you want to delete{' '}
            <span className="font-medium text-gray-900">{currentProject?.name}</span>?
            This will also delete all associated issues.
          </p>

          {deleteProject.isError && (
            <div className="rounded-md bg-red-50 px-3 py-2 text-xs text-red-600">
              {(deleteProject.error as Error).message}
            </div>
          )}

          <div className="flex justify-end gap-2 pt-1">
            <button
              onClick={() => setDeleteConfirmOpen(false)}
              className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 transition-colors"
            >
              Cancel
            </button>
            <button
              onClick={handleDelete}
              disabled={deleteProject.isPending}
              className="rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50 transition-colors"
            >
              {deleteProject.isPending ? 'Deleting...' : 'Delete'}
            </button>
          </div>
        </div>
      </Dialog>
    </>
  )
}
